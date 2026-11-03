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
  const name = bytes.subarray(cursor + 46, cursor + 46 + nameLength).toString('utf8');
  if (/\.(command|sh)$/.test(name)) {
    bytes[cursor + 5] = 3; // Unix creator platform
    bytes.writeUInt32LE((0o100755 << 16) >>> 0, cursor + 38);
    fixed++;
  }
  cursor += 46 + nameLength + extraLength + commentLength;
}
await writeFile(path, bytes);
console.log('Marked ' + fixed + ' Mac scripts executable in ZIP.');
