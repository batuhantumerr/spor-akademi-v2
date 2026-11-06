import { ApiProblem, fieldErrors, problemMessage } from '../../core/api/problem';
import { ui } from '../../core/i18n/tr';

/** Where an enrollment problem is shown: on the picker, on the start date, or above the form. */
export type EnrollTarget = 'group' | 'student' | 'startDate' | null;

/** Problems about the student/group pair: shown on whatever the dialog lets the user pick. */
const pairCodes = new Set([
  'Enrollment.BirthYearOutOfRange',
  'Enrollment.ScheduleConflict',
  'Enrollment.AlreadyEnrolled',
]);
const groupCodes = new Set(['Enrollment.GroupFull', 'Enrollment.GroupArchived', 'Group.NotFound']);
const studentCodes = new Set(['Enrollment.StudentNotActive', 'Student.NotFound']);

/**
 * Maps an enroll failure to the control it concerns. `picker` is what the dialog lets the user
 * choose (a group for a student, a student for a group); anything else, such as Lock.Timeout
 * ("try again in a few seconds"), stays a form-level message and the form stays as it was.
 */
export function enrollProblem(
  problem: ApiProblem,
  picker: 'group' | 'student',
): { target: EnrollTarget; message: string } {
  if (fieldErrors(problem)['startDate']) {
    return { target: 'startDate', message: ui.enrollments.startDateHint };
  }
  const message = problemMessage(problem);
  if (pairCodes.has(problem.code)) {
    return { target: picker, message };
  }
  if (groupCodes.has(problem.code) && picker === 'group') {
    return { target: 'group', message };
  }
  if (studentCodes.has(problem.code) && picker === 'student') {
    return { target: 'student', message };
  }
  return { target: null, message };
}
