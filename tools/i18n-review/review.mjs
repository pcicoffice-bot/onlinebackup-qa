// UI-030: the AI reads every translation of the product next to its English source and reports what a native speaker
// would correct: a wrong meaning, an inconsistent or unusual term, a text much longer than the English (buttons and
// menus), a broken placeholder. It never changes the dictionaries — the report (review/<lang>.json + review/index.html)
// is for fixing. Needs ANTHROPIC_API_KEY. Languages: the arguments, or all.
import Anthropic from "@anthropic-ai/sdk";
import { z } from "zod";
import { zodOutputFormat } from "@anthropic-ai/sdk/helpers/zod";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const dictDir = path.join(here, "..", "..", "src", "Core", "i18n");
const out = path.resolve(process.env.OUT || "review");
const all = ["he", "ar", "de", "es", "pt", "fr", "it", "nl", "pl", "tr", "ja", "zh"];
const langs = process.argv.slice(2).length ? process.argv.slice(2) : all;
const client = new Anthropic();

const Issues = z.object({
  issues: z.array(z.object({
    english: z.string().describe("the English source text, exactly as given"),
    translation: z.string().describe("the current translation, exactly as given"),
    problem: z.enum(["meaning", "terminology", "too long", "placeholder", "grammar", "register", "untranslated"]),
    explanation: z.string().describe("one short sentence in English: what is wrong"),
    suggestion: z.string().describe("the corrected translation (keep {0}, {1} … exactly)"),
  })),
});

const system = "You review the user-interface translations of an online backup product sold to IT companies (servers, backup sets, restore, encryption, licences). "
  + "For each English → translation pair, report only real problems a native-speaking IT professional would fix: wrong or misleading meaning, a term that differs from the usual software term in that language or from the term used for the same English word elsewhere in the list, "
  + "a translation much longer than needed for a button or menu (the English is short), placeholders {0} {1} changed or missing, grammar, an inconsistent level of formality, or text left in English. "
  + "Product and technology names (Microsoft 365, VMware, restic, AES-256, Windows …) stay as they are. Report nothing for good translations.";

fs.mkdirSync(out, { recursive: true });
const summary = [];
for (const lang of langs) {
  const d = JSON.parse(fs.readFileSync(path.join(dictDir, lang + ".json"), "utf8"));
  const pairs = Object.entries(d.strings);
  const found = [];
  for (let i = 0; i < pairs.length; i += 120) {
    const batch = pairs.slice(i, i + 120).map(([en, tr], n) => (i + n) + ". " + JSON.stringify(en) + " → " + JSON.stringify(tr)).join("\n");
    const r = await client.messages.parse({
      model: "claude-opus-5-5",
      max_tokens: 16000,
      system,
      messages: [{ role: "user", content: "Language: " + d.name + " (" + lang + ", " + (d.dir || "ltr") + ")\n\n" + batch }],
      output_config: { format: zodOutputFormat(Issues) },
    });
    if (r.stop_reason === "refusal") { console.error(lang + ": refused at " + i); continue; }
    if (r.parsed_output) found.push(...r.parsed_output.issues);
    process.stdout.write(".");
  }
  fs.writeFileSync(path.join(out, lang + ".json"), JSON.stringify(found, null, 1));
  summary.push([lang, d.name, pairs.length, found]);
  console.log("\n" + lang + ": " + found.length + " suggestions of " + pairs.length + " texts");
}
const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
fs.writeFileSync(path.join(out, "index.html"), '<!doctype html><meta charset="utf-8"><title>Translation review</title><style>body{font:14px system-ui;margin:20px}td,th{border-bottom:1px solid #ddd;padding:4px 6px;text-align:start;vertical-align:top}</style><h1>Translation review</h1>'
  + summary.map(([l, n, c, f]) => "<h2>" + esc(n) + " — " + f.length + " / " + c + "</h2><table><tr><th>English</th><th>Now</th><th>Problem</th><th>Suggestion</th></tr>"
    + f.map((x) => "<tr><td>" + esc(x.english) + '</td><td dir="auto">' + esc(x.translation) + "</td><td>" + esc(x.problem) + ": " + esc(x.explanation) + '</td><td dir="auto">' + esc(x.suggestion) + "</td></tr>").join("") + "</table>").join(""));
console.log("report: " + path.join(out, "index.html"));
