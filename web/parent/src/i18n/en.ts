import type { ErrorMessageText, ParentText } from './tr';

/**
 * English text of the parent app (P3-10, DEC-426). Typed against tr.ts, so a missing or extra key is a compile
 * error; open maps and lists are checked by i18n.test.ts. Glossary (DEC-427): Aidat = monthly fee,
 * Yoklama = attendance, Veli = parent (in this app the reader is the parent).
 */
export const enText: ParentText = {
  app: {
    name: 'Spor Akademi',
    tagline: 'Parent',
    skipToContent: 'Skip to content',
    mainNavigation: 'Main menu',
    loading: 'Loading…',
    retry: 'Try again',
    back: 'Back',
  },
  theme: {
    toggle: 'Change theme',
    light: 'Switch to light theme',
    dark: 'Switch to dark theme',
  },
  language: {
    change: 'Change language',
  },
  nav: {
    children: 'My children',
    logout: 'Sign out',
  },
  login: {
    title: 'Parent sign-in',
    subtitle: 'Sign in to see your children’s schedule, attendance and monthly fees.',
    email: 'Email',
    password: 'Password',
    showPassword: 'Show password',
    hidePassword: 'Hide password',
    submit: 'Sign in',
    submitting: 'Signing in…',
    forgotPassword: 'Forgot password',
    expired: 'Your session has expired. Please sign in again.',
    invitationHint:
      'If you received an invitation email, first set your password with the link in that email.',
  },
  forgotPassword: {
    title: 'Forgot password',
    subtitle: 'Enter your account’s email address and we’ll send you a link to set a new password.',
    email: 'Email',
    submit: 'Send link',
    submitting: 'Sending…',
    sent: 'If an account is registered with this address, you’ll receive an email within a few minutes. The link is valid for 30 minutes and can be used only once.',
    backToLogin: 'Back to sign in',
  },
  resetPassword: {
    title: 'Set your password',
    subtitle:
      'Set a password for your account with your invitation or password reset link. Changing your password signs you out of your open sessions.',
    next: 'New password',
    confirm: 'Confirm new password',
    hint: 'At least 12 characters. Common passwords are not accepted.',
    mismatch: 'The passwords don’t match.',
    submit: 'Save password',
    submitting: 'Saving…',
    missingToken: 'The link is missing or incorrect. Open the link in the email or request a new one.',
    requestNew: 'Request a new link',
    success: 'Your password has been saved. You can now sign in with your new password.',
    toLogin: 'Sign in',
  },
  children: {
    title: 'My children',
    empty:
      'No active student is linked to your account. If you think this is a mistake, please contact your school.',
    details: 'Details',
    detailsLabel: '{name}: details',
    nextTraining: 'Next training',
    noTraining: 'No training scheduled',
    today: 'Today',
    owed: 'Unpaid monthly fee',
    nothingOwed: 'No unpaid monthly fees',
    loadingSummary: 'Loading summary…',
    summaryFailed: 'The summary couldn’t be loaded.',
  },
  training: {
    today: 'Today {start}–{end}',
    inProgress: 'Now: {start}–{end}',
    onDay: '{day} {start}–{end}',
    place: '{group} · {facility}',
  },
  todayAttendance: {
    Present: 'Attended training today',
    Late: 'Arrived late to training today',
    Absent: 'Missed training today',
    Excused: 'Excused today',
    NotRecorded: 'Today’s attendance hasn’t been taken yet',
  },
  child: {
    sections: 'Sections',
    notFound: 'This student wasn’t found or is no longer linked to your account.',
    backToList: 'Back to my children',
    born: 'Date of birth: {date}',
  },
  dues: {
    title: 'Monthly fees',
    empty: 'No unpaid monthly fees.',
    total: 'Total unpaid: {amount}',
    period: 'Period',
    fee: 'Monthly fee',
    paid: 'Paid',
    remaining: 'Remaining',
    listFee: 'List fee',
    siblingDiscount: 'Sibling discount {percent}%',
    groupChange: 'Group change: this month was charged in the previous group',
    pay: 'Pay online',
    payLabel: 'Pay online: {period} monthly fee ({amount})',
    paying: 'Opening the payment page…',
    badCheckout: 'The payment page couldn’t be opened. Please try again.',
    payUnavailable:
      'Online payment isn’t available at your school right now. You can pay the fees at your school.',
  },
  schedule: {
    title: 'Weekly schedule',
    empty: 'Not enrolled in any active group.',
    coach: 'Coach: {name}',
    since: 'Since {date}',
  },
  attendance: {
    title: 'Attendance history',
    range: 'Last 30 days',
    empty: 'No training sessions in this period.',
    rate: 'Attendance rate: {rate}',
    counts: 'Attended: {present} · late: {late} · missed: {absent} · excused: {excused}',
    statuses: {
      Present: 'Attended',
      Late: 'Late',
      Absent: 'Missed',
      Excused: 'Excused',
      NotRecorded: 'Not taken',
    },
  },
  payments: {
    title: 'Payments',
    empty: 'No payments recorded.',
    methods: { Cash: 'Cash', BankTransfer: 'Bank transfer', Online: 'Online' },
    refunded: 'Refunded',
    refundedOn: 'Refunded to card: {date}',
    more: 'Show more',
  },
  announcements: {
    title: 'Announcements',
    empty: 'No announcements.',
    more: 'Show more',
  },
  sandbox: {
    title: 'Sandbox payment page',
    description:
      'This page imitates the test payment provider’s checkout. No real card details are asked for and no money is taken.',
    pay: 'Confirm payment',
    decline: 'Decline card',
    missingToken: 'The payment link is missing or invalid.',
    working: 'Processing…',
    badCallback: 'The payment provider’s return address is invalid. The payment was not completed.',
  },
  paymentResult: {
    title: 'Payment result',
    Succeeded: 'Your payment has been received. Thank you.',
    SucceededNote:
      'Once the payment provider confirms it, the payment appears in the payments list and is deducted from your monthly fee; this can take a few minutes.',
    Failed:
      'The payment couldn’t be completed. Check your card details and try again from the monthly fees screen.',
    Pending:
      'Your payment hasn’t completed yet. The result will show on your monthly fees screen once it arrives from the payment provider.',
    Expired: 'This payment page has expired. Start again from the monthly fees screen.',
    Unknown: 'Payment not found. Try again from the monthly fees screen.',
    back: 'Back to my children',
  },
  days: {
    Monday: 'Monday',
    Tuesday: 'Tuesday',
    Wednesday: 'Wednesday',
    Thursday: 'Thursday',
    Friday: 'Friday',
    Saturday: 'Saturday',
    Sunday: 'Sunday',
  },
  relationships: { Mother: 'Mother', Father: 'Father', Other: 'Guardian' },
  notFound: {
    title: 'Page not found',
    description: 'The page you are looking for may have moved or never existed.',
    backHome: 'Back to the home page',
  },
  errors: {
    unexpected: 'Something went wrong. Refresh the page and try again.',
  },
  pwa: {
    offlineTitle: 'No connection',
    offlineBody:
      'You seem to be offline. Your information is shown only while you are online; the page refreshes by itself once the connection is back.',
    offlineBanner: 'No internet connection. The information you see may be out of date.',
    updateReady: 'A new version is ready.',
    reload: 'Reload',
    installHint: 'Add Spor Akademi to your home screen to open it like an app.',
    install: 'Add to home screen',
    dismissInstall: 'Dismiss tip',
  },
  realtime: {
    yourChild: 'Your child',
    announcement: 'New announcement published',
    payment: 'Payment received for {name}',
    refund: 'Payment refunded for {name}',
    attendanceUpdated: 'Attendance updated for {name}',
    attendanceToday: {
      Present: '{name} attended training today',
      Absent: '{name} missed training today',
      Late: '{name} arrived late to training today',
      Excused: '{name} is excused from training today',
    },
    attendanceOnDate: {
      Present: '{name} attended training on {date}',
      Absent: '{name} missed training on {date}',
      Late: '{name} arrived late to training on {date}',
      Excused: '{name} was excused from training on {date}',
    },
  },
};

export const enErrorMessages: ErrorMessageText = {
  'Validation.Failed': 'Some fields in the form need to be corrected.',
  'Request.TooManyRequests': 'Too many attempts. Please wait a moment and try again.',
  'Resource.NotFound': 'The record you are looking for could not be found.',
  'Unexpected.Error':
    'Something went wrong. If the problem persists, send this code to your school: {traceId}',
  'Email.Invalid': 'Enter a valid email address.',
  'Field.Required': 'This field is required.',
  'Field.TooLong': 'This field is too long.',
  'Field.Invalid': 'This value is not valid.',
  'Auth.Unauthenticated': 'Sign in to continue.',
  'Auth.Forbidden': 'You don’t have permission to do this.',
  'Auth.InvalidCredentials': 'The email or password is incorrect.',
  'Auth.AccountLocked': 'Too many failed attempts. Your account is locked for 15 minutes.',
  'Auth.AccountInactive': 'Your account is not active. Please contact your school.',
  'Auth.SessionExpired': 'Your session has expired. Please sign in again.',
  'Auth.PasswordTooWeak': 'The password must be at least 12 characters and not a common password.',
  'Auth.PasswordUnchanged': 'The new password must be different from your current one.',
  'Auth.ResetTokenInvalid': 'This link is invalid, already used or expired. Request a new set-password link.',
  'Student.NotFound': 'This student wasn’t found or is no longer linked to your account.',
  'Enrollment.NotFound': 'This enrolment wasn’t found.',
  'OnlinePayment.NotFound': 'Online payment not found.',
  'OnlinePayment.NothingDue': 'Nothing is left to pay for this period.',
  'OnlinePayment.PeriodInFuture': 'The monthly fee of a future period can’t be paid yet.',
  'OnlinePayment.CheckoutInProgress':
    'A payment for this monthly fee has already been started. Try again in a few minutes.',
  'OnlinePayment.NotOpen': 'This online payment is no longer open.',
  'OnlinePayment.ProviderUnavailable':
    'The payment provider can’t be reached right now. Please try again in a moment.',
  'OnlinePayment.Disabled': 'Online payment isn’t available right now. Please contact your school.',
  'Idempotency.KeyReused':
    'This payment request has already been used for another operation. Refresh the page.',
  'Idempotency.InProgress': 'The same operation is in progress. Try again in a few seconds.',
  'Network.Unavailable': 'The server can’t be reached. Check your internet connection and try again.',
  'ParentApp.NotParent':
    'This app is for parents. School administrators and coaches sign in to the management panel.',
  'ParentApp.PasswordSetupRequired':
    'To continue, set your password with the invitation link in your email. If the link has expired, ask your school for a new invitation.',
};
