using YobaLog.Core.Auth;
using YobaLog.Web;

if (args.Length >= 2 && args[0] == "--hash-password")
{
	Console.WriteLine(AdminPasswordHasher.Hash(args[1]));
	return;
}

var builder = WebApplication.CreateBuilder(args);
YobaLogApp.ConfigureServices(builder);

var app = builder.Build();
YobaLogApp.Configure(app);
app.Run();

public partial class Program;
