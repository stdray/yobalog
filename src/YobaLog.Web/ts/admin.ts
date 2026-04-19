export const version = "0.0.0" as const;

declare global {
	interface Window {
		htmx?: { process(el: Element): void };
	}
}

// ---------- KQL completion ----------

document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	const button = target?.closest(".kql-suggestion") as HTMLButtonElement | null;
	if (!button) return;

	const list = button.closest("[data-kql-completions]") as HTMLElement | null;
	const textarea = document.getElementById("kql-textarea") as HTMLTextAreaElement | null;
	if (!list || !textarea) return;

	const editStart = Number(list.dataset["editStart"] ?? "0");
	const editLength = Number(list.dataset["editLength"] ?? "0");
	const before = button.dataset["before"] ?? "";
	const after = button.dataset["after"] ?? "";

	const value = textarea.value;
	const left = value.substring(0, editStart);
	const right = value.substring(editStart + editLength);
	textarea.value = left + before + after + right;

	const caret = left.length + before.length;
	textarea.setSelectionRange(caret, caret);
	textarea.focus();

	const panel = document.getElementById("kql-completions");
	if (panel) panel.innerHTML = "";
});

document.addEventListener("keydown", (event) => {
	if (event.key !== "Escape") return;
	const panel = document.getElementById("kql-completions");
	if (panel) panel.innerHTML = "";
});

// ---------- Copy-to-clipboard ----------

document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	const btn = target?.closest("[data-copy]") as HTMLButtonElement | null;
	if (!btn) return;
	event.stopPropagation();
	event.preventDefault();

	const text = btn.dataset["copy"] ?? "";
	void navigator.clipboard.writeText(text).then(() => {
		const original = btn.textContent;
		btn.textContent = "copied";
		btn.dataset["state"] = "copied";
		setTimeout(() => {
			btn.textContent = original;
			btn.removeAttribute("data-state");
		}, 1200);
	});
});

// ---------- Live-tail toggle ----------

document.addEventListener("change", (event) => {
	const target = event.target as HTMLInputElement | null;
	if (target?.id !== "live-tail-toggle") return;

	const wsId = target.dataset["workspace"];
	const kql = target.dataset["kql"] ?? "";
	const tbody = document.getElementById("events-body");
	if (!wsId || !tbody?.parentElement) return;

	const containerId = "live-tail-sse";
	document.getElementById(containerId)?.remove();

	if (!target.checked) return;

	const url = `/api/ws/${encodeURIComponent(wsId)}/tail?kql=${encodeURIComponent(kql)}`;
	const container = document.createElement("div");
	container.id = containerId;
	container.setAttribute("hx-ext", "sse");
	container.setAttribute("sse-connect", url);
	container.innerHTML = '<div sse-swap="event" hx-target="#events-body" hx-swap="afterbegin"></div>';
	tbody.parentElement.parentElement?.insertBefore(container, tbody.parentElement);
	window.htmx?.process(container);
});

// ---------- Share as TSV modal ----------

document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	if (!target) return;

	const openBtn = target.closest("[data-share-modal-open]") as HTMLElement | null;
	if (openBtn) {
		const modal = document.getElementById("share-modal") as HTMLDialogElement | null;
		modal?.showModal();
		const result = document.getElementById("share-result");
		const error = document.getElementById("share-error");
		result?.classList.add("hidden");
		error?.classList.add("hidden");
		return;
	}

	const ttlBtn = target.closest("[data-ttl]") as HTMLElement | null;
	if (ttlBtn) {
		const group = ttlBtn.closest("[data-share-ttl-group]");
		if (group) {
			for (const b of group.querySelectorAll("[data-ttl]")) b.classList.remove("btn-active");
		}
		ttlBtn.classList.add("btn-active");
		return;
	}

	if (target.id === "share-generate") {
		void generateShare();
		return;
	}

	if (target.closest("[data-share-copy]")) {
		const url = (document.getElementById("share-url") as HTMLInputElement | null)?.value ?? "";
		void navigator.clipboard.writeText(url);
		return;
	}
});

async function generateShare(): Promise<void> {
	const trigger = document.querySelector("[data-share-modal-open]") as HTMLElement | null;
	const wsId = trigger?.dataset["workspace"];
	const kql = trigger?.dataset["kql"] ?? "LogEvents";
	if (!wsId) return;

	const ttlAttr = document.querySelector("[data-share-ttl-group] .btn-active") as HTMLElement | null;
	const ttlHours = Number(ttlAttr?.dataset["ttl"] ?? "24");

	const modes: Record<string, string> = {};
	const columns: string[] = ["Id", "Timestamp", "Level"];
	const seen = new Set<string>(columns);
	for (const r of document.querySelectorAll<HTMLInputElement>("[data-share-radio]:checked")) {
		const path = r.dataset["path"];
		const value = r.value;
		if (!path) continue;
		if (!seen.has(path)) {
			columns.push(path);
			seen.add(path);
		}
		if (value !== "keep") modes[path] = value;
	}

	const result = document.getElementById("share-result");
	const error = document.getElementById("share-error");
	result?.classList.add("hidden");
	error?.classList.add("hidden");

	try {
		const resp = await fetch(`/api/ws/${encodeURIComponent(wsId)}/share`, {
			method: "POST",
			headers: { "Content-Type": "application/json" },
			body: JSON.stringify({ kql, ttlHours, columns, modes, savePolicy: true }),
		});
		if (!resp.ok) {
			const text = await resp.text();
			if (error) {
				error.textContent = `Error: ${resp.status} ${text}`;
				error.classList.remove("hidden");
			}
			return;
		}
		const body = (await resp.json()) as { url: string; expiresAt: string };
		const urlInput = document.getElementById("share-url") as HTMLInputElement | null;
		const expiresEl = document.getElementById("share-expires");
		if (urlInput) urlInput.value = body.url;
		if (expiresEl) expiresEl.textContent = new Date(body.expiresAt).toLocaleString();
		result?.classList.remove("hidden");
	} catch (e) {
		if (error) {
			error.textContent = `Network error: ${String(e)}`;
			error.classList.remove("hidden");
		}
	}
}

// ---------- Expandable event row ----------

document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	if (!target) return;

	// Don't toggle when clicking inside interactive children.
	if (target.closest("button, a, input, textarea, select, summary")) return;

	const row = target.closest("tr[data-event-id]") as HTMLTableRowElement | null;
	if (!row) return;

	const details = row.nextElementSibling as HTMLElement | null;
	if (details?.classList.contains("event-details")) {
		details.classList.toggle("hidden");
	}
});
