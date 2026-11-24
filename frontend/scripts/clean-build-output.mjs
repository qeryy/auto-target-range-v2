import { readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";

const outputs = [
  "../../src/spiritscout/web/static/app/session.css",
  "../../src/spiritscout/web/static/app/session.js",
];

for (const relativePath of outputs) {
  const outputPath = resolve(import.meta.dirname, relativePath);
  const source = await readFile(outputPath, "utf8");
  let cleaned = source.replace(/[ \t]+(?=\r?\n)/g, "").replace(/\r\n/g, "\n");
  if (!cleaned.endsWith("\n")) {
    cleaned += "\n";
  }
  if (cleaned !== source) {
    await writeFile(outputPath, cleaned, "utf8");
  }
}
