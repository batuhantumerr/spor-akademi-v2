import { expect, request } from '@playwright/test';

interface MailpitSearch {
  messages: { ID: string; Subject: string }[];
}

interface MailpitMessage {
  Text: string;
}

function mailpitUrl(): string {
  const url = process.env['E2E_MAILPIT_URL'];
  if (!url) {
    throw new Error('E2E_MAILPIT_URL is not set; run the suite with "bash scripts/e2e.sh".');
  }
  return url;
}

/**
 * Waits for the newest e-mail to `address` in the run's Mailpit catcher (compose `mail` profile, nothing is
 * relayed) and returns the first link in its text body that matches `link`. The API sends e-mail from the outbox
 * after the commit (every 10 s by default), so this polls. Addresses are unique per run and project (.test domain).
 */
export async function waitForLink(
  address: string,
  link: RegExp,
  timeout = 60_000,
): Promise<string> {
  const mailpit = await request.newContext({ baseURL: mailpitUrl() });
  try {
    let found: string | undefined;
    await expect
      .poll(
        async () => {
          const search = await mailpit.get('/api/v1/search', {
            params: { query: `to:"${address}"` },
          });
          if (!search.ok()) return undefined;
          const [newest] = ((await search.json()) as MailpitSearch).messages;
          if (!newest) return undefined;
          const message = await mailpit.get(`/api/v1/message/${newest.ID}`);
          found = ((await message.json()) as MailpitMessage).Text.match(link)?.[0];
          return found;
        },
        { message: `an e-mail to ${address} with a link`, timeout, intervals: [1_000, 2_000] },
      )
      .toBeDefined();
    return found!;
  } finally {
    await mailpit.dispose();
  }
}
