import { HttpErrorResponse } from '@angular/common/http';

import { fieldErrors, problemMessage, toProblem } from './problem';

describe('problem mapping', () => {
  it('uses the API code and fills arguments', () => {
    const problem = toProblem(
      new HttpErrorResponse({
        status: 422,
        error: { code: 'Group.CapacityBelowActiveEnrollments', args: { count: 12 } },
      }),
    );
    expect(problemMessage(problem)).toBe(
      'Kontenjan, gruptaki aktif öğrenci sayısının (12) altına düşürülemez.',
    );
  });

  it('never shows internal details for unknown failures', () => {
    const problem = toProblem(
      new HttpErrorResponse({ status: 500, error: 'System.Exception: boom' }),
    );
    expect(problem.code).toBe('Unexpected.Error');
    expect(problemMessage(problem)).not.toContain('boom');
  });

  it('reports network failures separately', () => {
    expect(toProblem(new HttpErrorResponse({ status: 0 })).code).toBe('Network.Unavailable');
  });

  it('includes the trace id for unexpected errors', () => {
    const problem = toProblem(
      new HttpErrorResponse({ status: 500, error: { code: 'Unexpected.Error', traceId: 'abc' } }),
    );
    expect(problemMessage(problem)).toContain('abc');
  });

  it('translates field errors and falls back for unknown codes', () => {
    const errors = fieldErrors({
      code: 'Validation.Failed',
      errors: { email: ['Email.Invalid'], name: ['Something.Unknown'] },
    });
    expect(errors['email']).toBe('Geçerli bir e-posta adresi girin.');
    expect(errors['name']).toBe('Bu alanın değeri geçerli değil.');
  });
});
