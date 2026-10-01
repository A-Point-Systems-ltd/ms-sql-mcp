import { test } from 'node:test';
import assert from 'node:assert/strict';
import { describeServerInfo } from '../out/connections/serverInfo.js';

test('reads data.server fields', () => {
  assert.equal(describeServerInfo({ success: true, data: { server: { productVersion: '16.0.1', serverName: 'SRV' } } }), 'Connected to SRV — SQL Server 16.0.1.');
});
test('tolerates missing data and PascalCase', () => {
  assert.equal(describeServerInfo({ Server: { ProductVersion: '15.0' } }), 'Connected to server — SQL Server 15.0.');
  assert.match(describeServerInfo({}), /no version/);
});

test('TLS-looking errors get a hint, others do not', async () => {
  const { withConnectionHint } = await import('../out/connections/serverInfo.js');
  assert.match(withConnectionHint('The certificate chain was issued by an authority that is not trusted'), /Tip: edit the connection/);
  assert.equal(withConnectionHint('Login failed for user x'), 'Login failed for user x');
});
