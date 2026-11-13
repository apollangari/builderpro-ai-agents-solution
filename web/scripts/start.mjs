// Cross-platform `ng serve` launcher (Windows cmd.exe can't expand ${PORT:-4200}).
// Aspire sets PORT for the "web" resource; without Aspire we default to 4200.
import { spawn } from 'node:child_process';

const port = process.env.PORT ?? '4200';
const child = spawn('npx', ['ng', 'serve', '--port', port, '--host', '0.0.0.0'], {
  stdio: 'inherit',
  shell: process.platform === 'win32',
});
child.on('exit', (code) => process.exit(code ?? 0));
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => child.kill(signal));
