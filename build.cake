#addin nuget:?package=Cake.Docker&version=1.5.0-beta.1&prerelease
#tool dotnet:?package=GitVersion.Tool&version=6.4.0

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var dockerImage = Argument("dockerImage", "yobalog-web");
var dockerTagArgument = Argument("dockerTag", string.Empty);
var dockerPushEnabled = Argument("dockerPush", false);
var ghcrRepositoryArgument = Argument("ghcrRepository", string.Empty);
var dockerTagOutputArgument = Argument("dockerTagOutput", string.Empty);

var solution = "./YobaLog.slnx";
var webProject = "./src/YobaLog.Web/YobaLog.Web.csproj";
var unitTestProject = "./tests/YobaLog.Tests/YobaLog.Tests.csproj";
var e2eTestProject = "./tests/YobaLog.E2ETests/YobaLog.E2ETests.csproj";
var dockerFile = "./src/YobaLog.Web/Dockerfile";

GitVersion gitVersion = null;
var computedDockerTag = "latest";

DotNetBuildSettings CreateVersionedBuildSettings(string buildVersion, string shortSha, string commitDate) =>
	new()
	{
		Configuration = configuration,
		NoRestore = true,
		MSBuildSettings = new DotNetMSBuildSettings()
			.WithProperty("Version", buildVersion)
			.WithProperty("InformationalVersion", $"{buildVersion} ({shortSha}, {commitDate})")
			.WithProperty("GitShortSha", shortSha)
			.WithProperty("GitCommitDate", commitDate)
	};

Task("Clean")
	.Does(() =>
{
	DotNetClean(solution, new DotNetCleanSettings { Configuration = configuration });
});

Task("Restore")
	.IsDependentOn("Clean")
	.Does(() =>
{
	DotNetRestore(solution);
});

Task("Version")
	.IsDependentOn("Restore")
	.Does(() =>
{
	gitVersion = GitVersion(new GitVersionSettings
	{
		OutputType = GitVersionOutput.Json,
		NoFetch = true
	});

	Information("GitVersion FullSemVer: {0}", gitVersion.FullSemVer);
	Information("GitVersion ShortSha: {0}", gitVersion.ShortSha);
	Information("GitVersion CommitDate: {0}", gitVersion.CommitDate);
});

Task("Build")
	.IsDependentOn("Version")
	.Does(() =>
{
	var buildVersion = gitVersion.FullSemVer;
	DotNetBuild(solution, CreateVersionedBuildSettings(buildVersion, gitVersion.ShortSha, gitVersion.CommitDate));
});

// Fast lane: YobaLog.Tests (unit) only. E2E lives in a separate target because it needs a
// Playwright Chromium download (~200MB) that shouldn't block main builds; CI runs it in a
// parallel job and gates Docker push on both.
Task("Test")
	.IsDependentOn("Build")
	.Does(() =>
{
	DotNetTest(unitTestProject, new DotNetTestSettings
	{
		Configuration = configuration,
		NoBuild = true
	});
});

// E2E task: requires `pwsh bin/Debug/net10.0/playwright.ps1 install chromium` on the runner.
// Run locally with `./build.sh --target=E2ETest` after a successful Build. Cake `E2ETest`
// depends on Build (not Test) so the two test tasks run parallel in CI without duplicating
// the compile step.
Task("E2ETest")
	.IsDependentOn("Build")
	.Does(() =>
{
	DotNetTest(e2eTestProject, new DotNetTestSettings
	{
		Configuration = configuration,
		NoBuild = true
	});
});

Task("Docker")
	.IsDependentOn("Test")
	.Does(() =>
{
	var gitVersionTag = gitVersion.FullSemVer.Replace('+', '-');
	var finalTag = string.IsNullOrWhiteSpace(dockerTagArgument) ? gitVersionTag : dockerTagArgument;
	computedDockerTag = finalTag;
	var imageWithTag = $"{dockerImage}:{finalTag}";

	if (!string.IsNullOrWhiteSpace(dockerTagOutputArgument))
	{
		var outputPath = MakeAbsolute(FilePath.FromString(dockerTagOutputArgument));
		EnsureDirectoryExists(outputPath.GetDirectory());
		System.IO.File.WriteAllText(outputPath.FullPath, finalTag);
	}

	Information("Building Docker image {0}", imageWithTag);

	var buildSettings = new DockerImageBuildSettings
	{
		File = dockerFile,
		Tag = new[] { imageWithTag },
		BuildArg = new[]
		{
			$"APP_VERSION={gitVersion.FullSemVer}",
			$"GIT_SHORT_SHA={gitVersion.ShortSha}",
			$"GIT_COMMIT_DATE={gitVersion.CommitDate}"
		}
	};

	DockerBuild(buildSettings, ".");
});

// Smoke-test the chiseled runtime: launch container, wait for HTTP 200 on /Login (unauth root
// redirects there, chiseled has no shell for docker-exec debugging so failures only surface at
// runtime). 30s total timeout. yobalog differs from yobaconf in that `/` requires auth → the
// smoke endpoint is /Login which is anonymous.
Task("DockerSmoke")
	.IsDependentOn("Docker")
	.Does(() =>
{
	var imageWithTag = $"{dockerImage}:{computedDockerTag}";
	var containerName = $"yobalog-smoke-{Guid.NewGuid():N}".Substring(0, 30);

	Information("Starting smoke-test container {0}", containerName);
	var runExit = StartProcess("docker", new ProcessSettings
	{
		Arguments = $"run -d --name {containerName} -p 8080:8080 -e Admin__Username=smoke -e Admin__Password=smoke {imageWithTag}"
	});
	if (runExit != 0)
		throw new CakeException($"docker run failed with exit code {runExit}");

	try
	{
		var healthy = false;
		for (var i = 1; i <= 30; i++)
		{
			System.Threading.Thread.Sleep(1000);
			var curlExit = StartProcess("curl", new ProcessSettings
			{
				// 127.0.0.1 not localhost — Docker Desktop's port forwarding on Windows binds IPv4
				// only, curl resolving localhost → ::1 times out on every probe and blows the 30s
				// budget. `-f` drops on any 4xx/5xx; `-s` silent; skip `-L` (no redirect to follow
				// on /Login — it's anonymous + 200 when unauthenticated). `--max-time 2` bounds
				// each probe so a mid-boot hang doesn't eat the whole loop. `-o NUL` on Windows;
				// Cake's RedirectStandardOutput uses ProcessStartInfo under the hood and Windows
				// `curl.exe` doesn't know `/dev/null`.
				Arguments = IsRunningOnWindows()
					? "-fsS --max-time 2 -o NUL http://127.0.0.1:8080/Login"
					: "-fsS --max-time 2 -o /dev/null http://127.0.0.1:8080/Login",
			});
			if (curlExit == 0)
			{
				Information("Smoke test passed after {0}s", i);
				healthy = true;
				break;
			}
		}

		if (!healthy)
		{
			StartProcess("docker", $"logs {containerName}");
			throw new CakeException("Container did not respond with 200 on /Login within 30s");
		}
	}
	finally
	{
		StartProcess("docker", $"stop {containerName}");
		StartProcess("docker", $"rm {containerName}");
	}
});

Task("DockerPush")
	.IsDependentOn("DockerSmoke")
	.WithCriteria(() => dockerPushEnabled)
	.Does(() =>
{
	if (string.IsNullOrWhiteSpace(computedDockerTag))
		throw new CakeException("Docker tag was not computed. Ensure the Docker task ran successfully before pushing.");

	var sourceImage = $"{dockerImage}:{computedDockerTag}";

	var repository = ghcrRepositoryArgument;

	if (string.IsNullOrWhiteSpace(repository))
	{
		var githubRepositoryEnv = EnvironmentVariable("GITHUB_REPOSITORY");
		if (string.IsNullOrWhiteSpace(githubRepositoryEnv))
			throw new CakeException("dockerPush enabled but no ghcrRepository argument provided and GITHUB_REPOSITORY environment variable is missing. Provide --ghcrRepository or set GITHUB_REPOSITORY.");

		var imageName = dockerImage;
		if (imageName.Contains('/'))
			imageName = imageName.Substring(imageName.LastIndexOf('/') + 1);

		repository = $"ghcr.io/{githubRepositoryEnv.ToLowerInvariant()}/{imageName.ToLowerInvariant()}";
	}
	else if (!repository.StartsWith("ghcr.io/", StringComparison.OrdinalIgnoreCase))
	{
		repository = $"ghcr.io/{repository}";
	}

	var targetImage = $"{repository}:{computedDockerTag}";

	var ghcrUsername = EnvironmentVariable("GHCR_USERNAME");
	var ghcrToken = EnvironmentVariable("GHCR_TOKEN");

	if (!string.IsNullOrWhiteSpace(ghcrUsername) && !string.IsNullOrWhiteSpace(ghcrToken))
		DockerLogin("ghcr.io", ghcrUsername, ghcrToken);
	else
		Information("GHCR credentials not provided via GHCR_USERNAME/GHCR_TOKEN; assuming docker login already performed.");

	Information("Tagging {0} as {1}", sourceImage, targetImage);
	DockerTag(sourceImage, targetImage);

	Information("Pushing {0}", targetImage);
	DockerPush(targetImage);
});

// Single-window dev loop: bun watchers (ts + css via concurrently) and dotnet watch stream to
// the same terminal. Ctrl+C kills both process trees. Replaces the two-window run_dev.ps1.
// Uses System.Diagnostics.Process directly — Cake's IProcess lacks HasExited / tree-kill.
Task("Dev")
	.Does(() =>
{
	var webDir = MakeAbsolute(Directory("./src/YobaLog.Web")).FullPath;

	var frontend = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("bun", "run dev")
	{
		WorkingDirectory = webDir,
		UseShellExecute = false,
	});
	var backend = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", "watch --project src/YobaLog.Web")
	{
		UseShellExecute = false,
	});

	if (frontend == null || backend == null)
		throw new CakeException("Failed to start dev processes (bun / dotnet).");

	void KillAll()
	{
		try { if (!frontend.HasExited) frontend.Kill(entireProcessTree: true); } catch { }
		try { if (!backend.HasExited) backend.Kill(entireProcessTree: true); } catch { }
	}

	Console.CancelKeyPress += (_, e) =>
	{
		e.Cancel = true;
		KillAll();
	};

	Information("dev loop started (bun watchers + dotnet watch). Ctrl+C to stop.");

	// Poll until either child exits, then tear down the other. entireProcessTree=true covers
	// concurrently's ts/css sub-buns and dotnet watch's app child.
	while (!frontend.HasExited && !backend.HasExited)
	{
		System.Threading.Thread.Sleep(500);
	}

	KillAll();
});

Task("Default")
	.IsDependentOn("DockerPush");

RunTarget(target);
