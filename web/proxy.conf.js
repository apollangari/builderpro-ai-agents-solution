// Dev-server proxy: /api/* -> the GATEWAY that Aspire started (since increment 1c).
// The UI never talks to a service directly.
//
// Aspire injects the backend's address as an environment variable named
//   services__<resource-name>__<endpoint>__0
// (that is how Aspire service discovery works for non-.NET apps).
// Running `npm start` WITHOUT Aspire falls back to the gateway on localhost:5000.
const target =
  process.env['services__gateway__https__0'] ||
  process.env['services__gateway__http__0'] ||
  'http://localhost:5000';

module.exports = {
  '/api': {
    target,
    secure: false, // dev certificates
    changeOrigin: true,
    logLevel: 'info',
  },
};
