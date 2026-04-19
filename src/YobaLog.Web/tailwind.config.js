import daisyui from "daisyui";

/** @type {import('tailwindcss').Config} */
export default {
	// `.cs` scanned too: some HTML fragments (KQL completions) are built from C# string builders.
	content: ["./Pages/**/*.cshtml", "./Views/**/*.cshtml", "./ts/**/*.ts", "./**/*.cs"],
	theme: {
		extend: {},
	},
	plugins: [daisyui],
	daisyui: {
		themes: ["dark", "night", "business"],
		darkTheme: "dark",
		logs: false,
	},
};
