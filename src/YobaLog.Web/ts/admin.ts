export const version = "0.0.0" as const;

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
