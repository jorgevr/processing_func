#!/usr/bin/env node
/**
 * Seeds local Azurite with schema registry mappings for local development.
 * Run: node scripts/seed-azurite.js
 *
 * Requires: az CLI installed and on PATH.
 */
const { spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const SCHEMA_CONTAINER = 'schema-registry';
const BRONZE_CONTAINER = 'bronze';
const CONN_STR = 'UseDevelopmentStorage=true';

function az(...args) {
  const result = spawnSync('az', [...args, '--connection-string', CONN_STR], {
    encoding: 'utf8',
    windowsHide: true,
    shell: true,
  });
  if (result.error) throw result.error;
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

// Create containers
for (const container of [SCHEMA_CONTAINER, BRONZE_CONTAINER]) {
  console.log(`Creating container: ${container}`);
  const create = az('storage', 'container', 'create', '--name', container);
  if (create.status === 0) {
    console.log('  Container created (or already exists).');
  } else {
    console.error('  Failed:', create.stderr);
    process.exit(1);
  }
}

// Upload all schema files
const schemasDir = path.join(__dirname, '..', 'schemas');
const files = fs.readdirSync(schemasDir).filter(f => f.endsWith('.json'));

for (const file of files) {
  const filePath = path.join(schemasDir, file);
  console.log(`Uploading: ${file}`);
  const upload = az('storage', 'blob', 'upload',
    '--container-name', SCHEMA_CONTAINER,
    '--name', file,
    '--file', filePath,
    '--overwrite');
  if (upload.status === 0) {
    console.log('  OK');
  } else {
    console.error('  Failed:', upload.stderr);
    process.exit(1);
  }
}

// Upload fixture CSV for the send-test-message tool
// Path mirrors the storage_path hardcoded in scripts/send-test-message/Program.cs
const fixtureCsvPath = path.join(__dirname, 'fixtures', 'pvdaq-9068-sample.csv');
const fixtureBlobName =
  'source=pvdaq/dataset=9068_ac_power_data_20240101_20250430/' +
  'ingestion_date=2026-04-09/9068_ac_power_data_20240101_20250430_v1.csv';

console.log(`Uploading fixture CSV: ${fixtureBlobName}`);
const uploadFixture = az('storage', 'blob', 'upload',
  '--container-name', 'bronze',
  '--name', fixtureBlobName,
  '--file', fixtureCsvPath,
  '--overwrite');
if (uploadFixture.status === 0) {
  console.log('  OK');
} else {
  console.error('  Failed:', uploadFixture.stderr);
  process.exit(1);
}

console.log('\nDone. Schema registry and fixtures seeded.');
