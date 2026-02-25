const { Buffer } = require('node:buffer');
const { CozoDb } = require('.');

async function main() {
  const db = new CozoDb();
  try {
    const result = await db.run('?[] <- [["hello", "bun", $payload]]', {
      payload: Buffer.alloc(4, 7),
    });
    console.log(result.rows);
  } finally {
    db.close();
  }
}

main().catch((e) => {
  console.error(e.display || e.message || e);
  process.exitCode = 1;
});
