export const version = "0.0.0" as const;

declare global {
	interface Window {
		htmx?: { process(el: Element): void };
	}
}

// ---------- Local-time rendering ----------
// Server emits <time class="local-time" datetime="ISO-UTC">UTC-fallback</time>;
// we rewrite textContent into the viewer's timezone so fresh DOMs from htmx
// swaps / SSE get the same treatment. Format stays YYYY-MM-DD HH:mm:ss.SSS for
// consistency across locales — culture-aware formatting waits for the i18n
// scaffold (spec §9).

function formatLocalTime(iso: string): string {
	const d = new Date(iso);
	if (Number.isNaN(d.getTime())) return iso;
	const pad = (n: number, w = 2) => String(n).padStart(w, "0");
	return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`;
}

function renderLocalTimes(root: ParentNode): void {
	for (const el of root.querySelectorAll<HTMLElement>("time.local-time[datetime]")) {
		const iso = el.getAttribute("datetime");
		if (iso) el.textContent = formatLocalTime(iso);
	}
}

renderLocalTimes(document);
document.addEventListener("htmx:afterSwap", (event) => {
	const detail = (event as CustomEvent).detail as { target?: Element } | undefined;
	renderLocalTimes(detail?.target ?? document);
});

// ---------- Button press flash (yellow) ----------
// Replaces daisyUI's default :active scale-down on [data-flash] buttons with a clear
// yellow flash. Works for both mouse clicks and Ctrl+Enter (the latter calls flashButton
// directly so the flash shows during the submit navigation).

function flashButton(el: HTMLElement): void {
	el.classList.remove("btn-flash");
	// Force reflow to restart the CSS animation if the same button is pressed again rapidly.
	void el.offsetWidth;
	el.classList.add("btn-flash");
	setTimeout(() => el.classList.remove("btn-flash"), 500);
}

document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	const btn = target?.closest<HTMLElement>("[data-flash]");
	if (btn) flashButton(btn);
});

// ---------- Hotkey toast ----------

function showHotkeyToast(combo: string, action: string): void {
	const root = ensureToastRoot();
	const toast = document.createElement("div");
	toast.className =
		"alert alert-info py-1 px-2 shadow-lg flex-row items-center gap-2 opacity-0 transition-opacity duration-150 pointer-events-auto";

	const kbd = document.createElement("kbd");
	kbd.className = "kbd kbd-xs";
	kbd.textContent = combo;

	const text = document.createElement("span");
	text.className = "text-xs";
	text.textContent = action;

	toast.appendChild(kbd);
	toast.appendChild(text);
	root.appendChild(toast);

	requestAnimationFrame(() => toast.classList.remove("opacity-0"));
	setTimeout(() => {
		toast.classList.add("opacity-0");
		setTimeout(() => toast.remove(), 150);
	}, 1500);
}

function ensureToastRoot(): HTMLDivElement {
	const existing = document.getElementById("hotkey-toast-root") as HTMLDivElement | null;
	if (existing) return existing;
	const root = document.createElement("div");
	root.id = "hotkey-toast-root";
	root.className = "fixed bottom-4 right-4 z-50 flex flex-col gap-2 items-end pointer-events-none";
	document.body.appendChild(root);
	return root;
}

const PendingToastKey = "yobalog.pendingToast";

function deferHotkeyToast(combo: string, action: string): void {
	try {
		sessionStorage.setItem(PendingToastKey, JSON.stringify({ combo, action, t: Date.now() }));
	} catch {
		// sessionStorage unavailable (private mode / SecurityError) — just show now and hope navigation is slow.
		showHotkeyToast(combo, action);
	}
}

(() => {
	const raw = sessionStorage.getItem(PendingToastKey);
	if (!raw) return;
	sessionStorage.removeItem(PendingToastKey);
	try {
		const data = JSON.parse(raw) as { combo?: string; action?: string; t?: number };
		if (!data.combo || !data.action || typeof data.t !== "number") return;
		if (Date.now() - data.t > 3000) return;
		showHotkeyToast(data.combo, data.action);
	} catch {
		// malformed payload — drop silently.
	}
})();

// ---------- Global focus shortcut: "/" jumps to KQL textarea ----------

document.addEventListener("keydown", (event) => {
	if (event.key !== "/") return;
	if (event.ctrlKey || event.metaKey || event.altKey) return;
	const active = document.activeElement;
	if (active instanceof HTMLInputElement || active instanceof HTMLTextAreaElement) return;
	const textarea = document.getElementById("kql-textarea") as HTMLTextAreaElement | null;
	if (!textarea) return;
	event.preventDefault();
	textarea.focus();
	textarea.setSelectionRange(textarea.value.length, textarea.value.length);
	showHotkeyToast("/", "focus query");
});

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
	// Pure insertion (editLength === 0) adjacent to a non-separator produces things
	// like 'eventswhere' or 'count()by'. Prepend a space unless the char before the
	// cursor is already whitespace, an opening paren, or start-of-input.
	const prevChar = left.slice(-1);
	const needsLeadingSpace = editLength === 0 && prevChar !== "" && !/[\s(]/.test(prevChar);
	const prefix = needsLeadingSpace ? ` ${before}` : before;
	textarea.value = left + prefix + after + right;

	const caret = left.length + prefix.length;
	textarea.setSelectionRange(caret, caret);
	textarea.focus();

	const panel = document.getElementById("kql-completions");
	if (panel) panel.innerHTML = "";
});

document.addEventListener("keydown", (event) => {
	const target = event.target as HTMLElement | null;
	if (!(target instanceof HTMLTextAreaElement) || target.id !== "kql-textarea") {
		if (event.key === "Escape") closeKqlPanel();
		return;
	}

	if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
		event.preventDefault();
		closeKqlPanel();
		const submit = target.form?.querySelector<HTMLButtonElement>('button[type="submit"]');
		if (submit) flashButton(submit);
		// Toast would die mid-fade when requestSubmit() navigates; stash it and replay
		// from sessionStorage on the next page load so the user actually sees it.
		deferHotkeyToast(event.metaKey ? "⌘+Enter" : "Ctrl+Enter", "apply");
		target.form?.requestSubmit();
		return;
	}

	const items = Array.from(document.querySelectorAll<HTMLButtonElement>("#kql-completions .kql-suggestion"));

	if (event.key === "Escape") {
		closeKqlPanel();
		return;
	}

	if (items.length === 0) return;

	const current = items.findIndex((b) => b.dataset["kqlActive"] === "1");
	const cols = countKqlCols(items);
	const n = items.length;
	const start = current < 0 ? 0 : current;

	if (event.key === "ArrowRight") {
		event.preventDefault();
		highlightKqlItem(items, (start + 1) % n);
	} else if (event.key === "ArrowLeft") {
		event.preventDefault();
		highlightKqlItem(items, (start - 1 + n) % n);
	} else if (event.key === "ArrowDown") {
		event.preventDefault();
		highlightKqlItem(items, current < 0 ? 0 : (current + cols) % n);
	} else if (event.key === "ArrowUp") {
		event.preventDefault();
		highlightKqlItem(items, current < 0 ? n - 1 : (current - cols + n) % n);
	} else if (event.key === "Enter" && current >= 0) {
		event.preventDefault();
		items[current]?.click();
	}
});

function countKqlCols(items: readonly HTMLButtonElement[]): number {
	if (items.length < 2) return 1;
	const topRow = items[0]?.offsetTop ?? 0;
	let cols = 0;
	for (const item of items) {
		if (item.offsetTop === topRow) cols++;
		else break;
	}
	return cols || 1;
}

function closeKqlPanel(): void {
	const panel = document.getElementById("kql-completions");
	if (panel) panel.innerHTML = "";
}

// Click outside the panel or textarea → dismiss. Clicks on .kql-suggestion go through the insertion
// handler above (which closes the panel itself) before reaching here.
document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	if (!target) return;
	if (target.closest("#kql-completions") || target.closest("#kql-textarea")) return;
	closeKqlPanel();
});

function highlightKqlItem(items: readonly HTMLButtonElement[], i: number): void {
	for (let idx = 0; idx < items.length; idx++) {
		const btn = items[idx];
		if (!btn) continue;
		if (idx === i) {
			btn.dataset["kqlActive"] = "1";
			btn.classList.add("bg-primary", "text-primary-content");
			btn.scrollIntoView({ block: "nearest" });
		} else {
			btn.removeAttribute("data-kql-active");
			btn.classList.remove("bg-primary", "text-primary-content");
		}
	}
}

// ---------- Hover filter chips (✓/✗ over event cells) ----------

document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	const btn = target?.closest("[data-filter-field]") as HTMLButtonElement | null;
	if (!btn) return;
	event.stopPropagation();
	event.preventDefault();

	const field = btn.dataset["filterField"] ?? "";
	const op = btn.dataset["filterOp"] ?? "eq";
	const value = btn.dataset["filterValue"] ?? "";
	if (!field || !value) return;

	const sym = op === "eq" ? "==" : op === "ne" ? "!=" : op === "ge" ? ">=" : op === "le" ? "<=" : "==";

	const textarea = document.getElementById("kql-textarea") as HTMLTextAreaElement | null;
	if (!textarea) return;

	const base = textarea.value.trim().length > 0 ? textarea.value.trimEnd() : "events";
	textarea.value = `${base}\n| where ${field} ${sym} ${value}`;
	textarea.form?.requestSubmit();
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
	const kql = trigger?.dataset["kql"] ?? "events";
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
