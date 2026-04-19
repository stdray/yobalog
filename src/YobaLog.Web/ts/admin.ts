export const version = "0.0.0" as const;

// KQL completion apply: clicking a suggestion replaces the edit range
// with BeforeText + AfterText and positions the caret between them.
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

// Close completions on Escape.
document.addEventListener("keydown", (event) => {
	if (event.key !== "Escape") return;
	const panel = document.getElementById("kql-completions");
	if (panel) panel.innerHTML = "";
});
