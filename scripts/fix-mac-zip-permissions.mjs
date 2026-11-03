// Preserve executable .command/.sh files in ZIPs assembled on Windows.
import { readFile, writeFile } from 'node:fs/promises';
const path = process.argv[2];
if (!path) throw new Error('ZIP path is required.');
const bytes = await readFile(path);
let end = bytes.length - 22;
while (end >= Math.max(0, bytes.length - 65557) && bytes.readUInt32LE(end) !== 0x06054b50) end--;
if (end < 0 || bytes.readUInt32LE(end) !== 0x06054b50) throw new Error('ZIP central directory not found.');
const entries = bytes.readUInt16LE(end + 10);
let cursor = bytes.readUInt32LE(end + 16), fixed = 0;
for (let i = 0; i < entries; i++) {
  if (bytes.readUInt32LE(cursor) !== 0x02014b50) throw new Error('Invalid ZIP directory entry.');
  const nameLength = bytes.readUInt16LE(cursor + 28), extraLength = bytes.readUInt16LE(cursor + 30), commentLength = bytes.readUInt16LE(cursor + 32);
  const localOffset = bytes.readUInt32LE(cursor + 42);
  if (bytes.readUInt32LE(localOffset) !== 0x04034b50 || bytes.readUInt16LE(localOffset + 26) !== nameLength) throw new Error('Invalid ZIP local entry.');
  // Compress-Archive on Windows uses backslashes; ZIP paths must use '/' on Mac.
  for (const offset of [cursor + 46, localOffset + 30]) {
    for (let j = 0; j < nameLength; j++) if (bytes[offset + j] === 0x5c) bytes[offset + j] = 0x2f;
  }
  const name = bytes.subarray(cursor + 46, cursor + 46 + nameLength).toString('utf8');
  const executable = /\.(command|sh)$/.test(name), directory = name.endsWith('/');
  bytes[cursor + 5] = 3; // Unix creator platform
  const mode = directory ? 0o40755 : executable ? 0o100755 : 0o100644;
  bytes.writeUInt32LE(((mode << 16) | (directory ? 0x10 : 0)) >>> 0, cursor + 38);
  if (executable) fixed++;
  cursor += 46 + nameLength + extraLength + commentLength;
}
await writeFile(path, bytes);
console.log('Normalized ZIP paths and marked ' + fixed + ' Mac scripts executable.');
