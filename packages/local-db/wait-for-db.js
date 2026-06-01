const { Client } = require('pg');

async function waitForDatabase() {
  const maxRetries = 30;
  const retryDelay = 1000;

  for (let i = 0; i < maxRetries; i++) {
    const client = new Client({
      host: 'localhost',
      port: 43594,
      user: 'postgres',
      database: 'app',
    });

    try {
      await client.connect();
      console.log('✅ Database is ready!');
      await client.end();
      process.exit(0);
    } catch (error) {
      if (i === maxRetries - 1) {
        console.error('❌ Failed to connect to database after', maxRetries, 'attempts');
        process.exit(1);
      }
      console.log(`⏳ Waiting for database... (attempt ${i + 1}/${maxRetries})`);
      await new Promise((resolve) => setTimeout(resolve, retryDelay));
    }
  }
}

waitForDatabase();
