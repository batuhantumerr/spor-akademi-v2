/** Ports of the e2e API and of the app servers (e2e/serve.mjs), which proxy /api to the API. */
export const apiPort = 5180;
export const appPort = 4300;
/** The parent app (P3-05/P3-06), served the same way from web/parent/dist. */
export const parentPort = 4301;

export const apiUrl = `http://127.0.0.1:${apiPort}`;
export const appUrl = `https://localhost:${appPort}`;
export const parentUrl = `https://localhost:${parentPort}`;
