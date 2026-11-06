import { isTurkishPhone } from './validators';

describe('isTurkishPhone (same rule as the API)', () => {
  it.each(['0532 123 45 67', '5321234567', '+90 532 123 45 67', '90 (212) 555-12-34'])(
    'accepts %s',
    (value) => expect(isTurkishPhone(value)).toBe(true),
  );

  it.each([
    '12345',
    '0632 123 45 67',
    '+5321234567',
    '0532 123 45 6x',
    '+90+5321234567',
    '0532	123 45 67',
  ])('rejects %s', (value) => expect(isTurkishPhone(value)).toBe(false));
});
