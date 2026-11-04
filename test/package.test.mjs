/**
 * Package wiring: the contract the Harness relies on when it loads this bundle,
 * including the native launcher the Host half copies out of the package.
 *
 * These tests do not stand in for trying Ctrl+C / Ctrl+V / Ctrl+` in a browser;
 * they catch the failures a bad refactor produces early — a moved entry point, an
 * exports map that points nowhere, a client bundle registered under the wrong
 * name, or a launcher that no longer ships.
 */

import assert from "node:assert/strict";
import { readFile, stat } from "node:fs/promises";
import { dirname, join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";

const ROOT = dirname(dirname(fileURLToPath(import.meta.url)));

/** Read the package manifest. */
async function manifest() {
  return JSON.parse(await readFile(join(ROOT, "package.json"), "utf8"));
}

/** Resolve a package-relative path and report whether it exists. */
async function exists(path) {
  try {
    await stat(join(ROOT, path));
    return true;
  } catch {
    return false;
  }
}

const relative = (target) => target.replace(/^\.\//, "");

test("every export points at a file that exists", async () => {
  const pkg = await manifest();
  for (const [subpath, target] of Object.entries(pkg.exports)) {
    if (subpath.includes("*")) continue;
    assert.equal(await exists(relative(target)), true, `${subpath} -> ${target}`);
  }
  assert.equal(relative(pkg.exports["."]), pkg.main, "main and exports['.'] agree");
});

test("the bundle patch inserts this package", async () => {
  const pkg = await manifest();
  const patch = await readFile(join(ROOT, "cordis.patch.yml"), "utf8");
  assert.match(patch, /^- insert:/m);
  assert.match(
    patch,
    new RegExp(`name:\\s*["']${pkg.name}["']`),
    `cordis.patch.yml must insert ${pkg.name}`,
  );
  assert.equal(pkg.dsh.bundle.patch, "./cordis.patch.yml");
});

test("the client bundle registers the package name", async () => {
  const pkg = await manifest();
  const source = await readFile(join(ROOT, relative(pkg.exports["./client"])), "utf8");
  assert.match(source, /window\.__ModuleLoader__\.load\(/);
  assert.ok(
    source.includes(`id: '${pkg.name}'`) || source.includes(`id: "${pkg.name}"`),
    `the client bundle must register id ${pkg.name}`,
  );
});

test("the host half imports and exports apply plus inject", async () => {
  const pkg = await manifest();
  const host = await import(pathToFileURL(join(ROOT, relative(pkg.exports["."]))).href);
  assert.equal(typeof host.apply, "function");
  assert.ok(Array.isArray(host.inject));
});

test("the native launcher ships with the package", async () => {
  const pkg = await manifest();
  for (const path of [
    "shell/dsh-terminal-shell.exe",
    "shell/DshTerminalShell.cs",
    "shell/build.ps1",
  ]) {
    assert.ok(pkg.files.includes(path), `${path} must be published`);
    assert.equal(await exists(path), true, `${path} must exist`);
  }
});

test("the Plugin Manager metadata parses for every locale", async () => {
  const pkg = await manifest();
  assert.ok(Array.isArray(pkg.dsh.client.inject) && pkg.dsh.client.inject.length > 0);
  for (const locale of ["en", "zh"]) {
    const meta = JSON.parse(await readFile(join(ROOT, `locale/${locale}.json`), "utf8"));
    assert.equal(typeof meta.meta.title, "string");
    assert.equal(typeof meta.meta.description, "string");
  }
});
