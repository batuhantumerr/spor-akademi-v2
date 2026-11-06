/** Ports of the e2e API and of the admin app server (e2e/serve.mjs), which proxies /api to the API. */
export const apiPort = 5180;
export const appPort = 4300;

export const apiUrl = `http://127.0.0.1:${apiPort}`;
export const appUrl = `https://localhost:${appPort}`;
