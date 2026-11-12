import { enErrorMessages, enText } from './en';
import { localized } from './localized';

/**
 * All user-facing Turkish text of the parent app lives here; en.ts has the English text with exactly the same keys
 * (P3-10, DEC-426). Components read `text.<area>.<key>`, which follows the active language. Placeholders such as
 * `{name}` are filled with `format()`.
 */
const trText = {
  app: {
    name: 'Spor Akademi',
    tagline: 'Veli',
    skipToContent: 'İçeriğe geç',
    mainNavigation: 'Ana menü',
    loading: 'Yükleniyor…',
    retry: 'Tekrar dene',
    back: 'Geri',
  },
  theme: {
    toggle: 'Temayı değiştir',
    light: 'Açık temaya geç',
    dark: 'Koyu temaya geç',
  },
  language: {
    change: 'Dili değiştir',
  },
  nav: {
    children: 'Çocuklarım',
    logout: 'Çıkış yap',
  },
  login: {
    title: 'Veli girişi',
    subtitle: 'Çocuklarınızın programını, yoklamasını ve aidatlarını görmek için giriş yapın.',
    email: 'E-posta',
    password: 'Şifre',
    showPassword: 'Şifreyi göster',
    hidePassword: 'Şifreyi gizle',
    submit: 'Giriş yap',
    submitting: 'Giriş yapılıyor…',
    forgotPassword: 'Şifremi unuttum',
    expired: 'Oturumunuzun süresi doldu. Lütfen tekrar giriş yapın.',
    invitationHint: 'Davet e-postası aldıysanız önce e-postadaki bağlantıyla şifrenizi belirleyin.',
  },
  forgotPassword: {
    title: 'Şifremi unuttum',
    subtitle:
      'Hesabınızın e-posta adresini girin; yeni şifrenizi belirlemeniz için size bir bağlantı gönderelim.',
    email: 'E-posta',
    submit: 'Bağlantı gönder',
    submitting: 'Gönderiliyor…',
    sent: 'Bu adrese kayıtlı bir hesap varsa birkaç dakika içinde bir e-posta alacaksınız. Bağlantı 30 dakika geçerlidir ve yalnızca bir kez kullanılabilir.',
    backToLogin: 'Giriş sayfasına dön',
  },
  resetPassword: {
    title: 'Şifrenizi belirleyin',
    subtitle:
      'Davet veya şifre sıfırlama bağlantınızla hesabınız için bir şifre belirleyin. Şifreniz değiştiğinde açık oturumlarınız kapanır.',
    next: 'Yeni şifre',
    confirm: 'Yeni şifre (tekrar)',
    hint: 'En az 12 karakter. Yaygın şifreler kabul edilmez.',
    mismatch: 'Şifreler eşleşmiyor.',
    submit: 'Şifreyi kaydet',
    submitting: 'Kaydediliyor…',
    missingToken: 'Bağlantı eksik veya hatalı. E-postadaki bağlantıyı açın ya da yeni bir bağlantı isteyin.',
    requestNew: 'Yeni bağlantı iste',
    success: 'Şifreniz kaydedildi. Yeni şifrenizle giriş yapabilirsiniz.',
    toLogin: 'Giriş yap',
  },
  children: {
    title: 'Çocuklarım',
    empty:
      'Hesabınıza bağlı aktif bir öğrenci yok. Bir yanlışlık olduğunu düşünüyorsanız okulunuzla iletişime geçin.',
    details: 'Ayrıntılar',
    detailsLabel: '{name}: ayrıntılar',
    nextTraining: 'Sıradaki antrenman',
    noTraining: 'Planlanmış antrenman yok',
    today: 'Bugün',
    owed: 'Ödenmemiş aidat',
    nothingOwed: 'Ödenmemiş aidat yok',
    loadingSummary: 'Özet yükleniyor…',
    summaryFailed: 'Özet yüklenemedi.',
  },
  training: {
    today: 'Bugün {start}–{end}',
    inProgress: 'Şu anda: {start}–{end}',
    onDay: '{day} {start}–{end}',
    place: '{group} · {facility}',
  },
  todayAttendance: {
    Present: 'Bugün antrenmana katıldı',
    Late: 'Bugün antrenmana geç geldi',
    Absent: 'Bugün antrenmana katılmadı',
    Excused: 'Bugün izinli',
    NotRecorded: 'Bugünün yoklaması henüz alınmadı',
  } as Record<string, string>,
  child: {
    sections: 'Bölümler',
    notFound: 'Bu öğrenci bulunamadı veya artık hesabınıza bağlı değil.',
    backToList: 'Çocuklarıma dön',
    born: 'Doğum tarihi: {date}',
  },
  dues: {
    title: 'Aidatlar',
    empty: 'Ödenmemiş aidat yok.',
    total: 'Toplam ödenmemiş: {amount}',
    period: 'Dönem',
    fee: 'Aidat',
    paid: 'Ödenen',
    remaining: 'Kalan',
    listFee: 'Liste aidatı',
    siblingDiscount: 'Kardeş indirimi %{percent}',
    groupChange: 'Grup değişikliği: bu ay önceki grupta ücretlendirildi',
    pay: 'Online öde',
    payLabel: '{period} aidatını online öde ({amount})',
    paying: 'Ödeme sayfası açılıyor…',
    badCheckout: 'Ödeme sayfası açılamadı. Lütfen tekrar deneyin.',
  },
  schedule: {
    title: 'Haftalık program',
    empty: 'Kayıtlı olduğu aktif bir grup yok.',
    coach: 'Antrenör: {name}',
    since: '{date} tarihinden beri',
  },
  attendance: {
    title: 'Yoklama geçmişi',
    range: 'Son 30 gün',
    empty: 'Bu dönemde antrenman yok.',
    rate: 'Devam oranı: {rate}',
    counts: '{present} katıldı · {late} geç · {absent} gelmedi · {excused} izinli',
    statuses: {
      Present: 'Katıldı',
      Late: 'Geç geldi',
      Absent: 'Gelmedi',
      Excused: 'İzinli',
      NotRecorded: 'Alınmadı',
    } as Record<string, string>,
  },
  payments: {
    title: 'Ödemeler',
    empty: 'Kayıtlı ödeme yok.',
    methods: { Cash: 'Nakit', BankTransfer: 'Havale/EFT', Online: 'Online' } as Record<string, string>,
    refunded: 'İade edildi',
    refundedOn: 'Karta iade: {date}',
    more: 'Daha fazla göster',
  },
  announcements: {
    title: 'Duyurular',
    empty: 'Duyuru yok.',
    more: 'Daha fazla göster',
  },
  sandbox: {
    title: 'Sandbox ödeme sayfası',
    description:
      'Bu sayfa test ödeme sağlayıcısının ödeme ekranını taklit eder. Gerçek kart bilgisi istenmez ve para çekilmez.',
    pay: 'Ödemeyi onayla',
    decline: 'Kartı reddet',
    missingToken: 'Ödeme bağlantısı eksik veya geçersiz.',
    working: 'İşleniyor…',
    badCallback: 'Ödeme sağlayıcısının dönüş adresi geçersiz. Ödeme tamamlanmadı.',
  },
  paymentResult: {
    title: 'Ödeme sonucu',
    Succeeded: 'Ödemeniz alındı. Teşekkür ederiz.',
    // The page only shows the status from the redirect; the authoritative record is the payments list.
    SucceededNote:
      'Ödeme sağlayıcısı onayladığında ödemeler listesinde görünür ve aidatınızdan düşer; bu birkaç dakika sürebilir.',
    Failed: 'Ödeme tamamlanamadı. Kart bilgilerinizi kontrol edip aidat ekranından tekrar deneyin.',
    Pending:
      'Ödemeniz henüz sonuçlanmadı. Sonuç ödeme sağlayıcısından geldiğinde aidat ekranınızda görünecek.',
    Expired: 'Bu ödeme sayfasının süresi doldu. Aidat ekranından yeniden başlatın.',
    Unknown: 'Ödeme bulunamadı. Aidat ekranından yeniden deneyin.',
    back: 'Çocuklarıma dön',
  } as Record<string, string>,
  days: {
    Monday: 'Pazartesi',
    Tuesday: 'Salı',
    Wednesday: 'Çarşamba',
    Thursday: 'Perşembe',
    Friday: 'Cuma',
    Saturday: 'Cumartesi',
    Sunday: 'Pazar',
  } as Record<string, string>,
  relationships: { Mother: 'Anne', Father: 'Baba', Other: 'Veli' } as Record<string, string>,
  notFound: {
    title: 'Sayfa bulunamadı',
    description: 'Aradığınız sayfa taşınmış veya hiç var olmamış olabilir.',
    backHome: 'Ana sayfaya dön',
  },
  errors: {
    unexpected: 'Beklenmeyen bir hata oluştu. Sayfayı yenileyip tekrar deneyin.',
  },
  pwa: {
    offlineTitle: 'Bağlantı yok',
    offlineBody:
      'İnternet bağlantınız yok gibi görünüyor. Bilgileriniz yalnız bağlantı varken gösterilir; bağlantı gelince sayfa kendiliğinden yenilenir.',
    offlineBanner: 'İnternet bağlantısı yok. Gördüğünüz bilgiler güncel olmayabilir.',
    updateReady: 'Yeni sürüm hazır.',
    reload: 'Yenile',
    installHint: 'Spor Akademi’yi ana ekranınıza ekleyerek bir uygulama gibi açabilirsiniz.',
    install: 'Ana ekrana ekle',
    dismissInstall: 'İpucunu kapat',
  },
  // P3-08: real-time notices. Names come from the app's own children list, never from the hub's payload.
  realtime: {
    yourChild: 'Çocuğunuz',
    announcement: 'Yeni duyuru yayımlandı',
    payment: '{name} için ödeme alındı',
    refund: '{name} için ödeme iade edildi',
    attendanceUpdated: '{name} için yoklama güncellendi',
    attendanceToday: {
      Present: '{name} bugün antrenmana katıldı',
      Absent: '{name} bugün antrenmana katılmadı',
      Late: '{name} bugün antrenmana geç geldi',
      Excused: '{name} bugün antrenmanda izinli',
    },
    attendanceOnDate: {
      Present: '{name}, {date} antrenmanına katıldı',
      Absent: '{name}, {date} antrenmanına katılmadı',
      Late: '{name}, {date} antrenmanına geç geldi',
      Excused: '{name}, {date} antrenmanında izinli',
    },
  },
} as const;

/**
 * Turkish messages for the API's stable error codes (appendix E) the parent app can meet, plus the app's own
 * codes (`ParentApp.*`, `Network.*`). Unknown codes fall back to `Unexpected.Error`.
 */
const trErrorMessages = {
  'Validation.Failed': 'Formda düzeltilmesi gereken alanlar var.',
  'Request.TooManyRequests': 'Çok fazla deneme yapıldı. Lütfen biraz bekleyip tekrar deneyin.',
  'Resource.NotFound': 'Aradığınız kayıt bulunamadı.',
  'Unexpected.Error': 'Beklenmeyen bir hata oluştu. Sorun devam ederse bu kodu okulunuza iletin: {traceId}',
  'Email.Invalid': 'Geçerli bir e-posta adresi girin.',
  'Field.Required': 'Bu alan zorunludur.',
  'Field.TooLong': 'Bu alan çok uzun.',
  'Field.Invalid': 'Bu alanın değeri geçerli değil.',
  'Auth.Unauthenticated': 'Devam etmek için giriş yapın.',
  'Auth.Forbidden': 'Bu işlem için yetkiniz yok.',
  'Auth.InvalidCredentials': 'E-posta veya şifre hatalı.',
  'Auth.AccountLocked': 'Çok fazla hatalı deneme yapıldı. Hesabınız 15 dakika kilitlendi.',
  'Auth.AccountInactive': 'Hesabınız aktif değil. Okulunuzla iletişime geçin.',
  'Auth.SessionExpired': 'Oturumunuzun süresi doldu. Lütfen tekrar giriş yapın.',
  'Auth.PasswordTooWeak': 'Şifre en az 12 karakter olmalı ve çok yaygın bir şifre olmamalı.',
  'Auth.PasswordUnchanged': 'Yeni şifre mevcut şifrenizle aynı olamaz.',
  'Auth.ResetTokenInvalid':
    'Bu bağlantı geçersiz, kullanılmış veya süresi dolmuş. Yeni bir şifre belirleme bağlantısı isteyin.',
  'Student.NotFound': 'Bu öğrenci bulunamadı veya artık hesabınıza bağlı değil.',
  'Enrollment.NotFound': 'Bu kayıt bulunamadı.',
  'OnlinePayment.NotFound': 'Online ödeme bulunamadı.',
  'OnlinePayment.NothingDue': 'Bu dönem için ödenecek aidat kalmadı.',
  'OnlinePayment.PeriodInFuture': 'Gelecek bir dönemin aidatı henüz ödenemez.',
  'OnlinePayment.CheckoutInProgress':
    'Bu aidat için başlatılmış bir ödeme zaten var. Birkaç dakika sonra tekrar deneyin.',
  'OnlinePayment.NotOpen': 'Bu online ödeme artık açık değil.',
  'OnlinePayment.ProviderUnavailable':
    'Ödeme sağlayıcısına şu anda ulaşılamıyor. Lütfen biraz sonra tekrar deneyin.',
  'OnlinePayment.Disabled': 'Online ödeme şu anda kullanılamıyor. Okulunuzla iletişime geçin.',
  'Idempotency.KeyReused': 'Bu ödeme isteği başka bir işlem için kullanılmış. Sayfayı yenileyin.',
  'Idempotency.InProgress': 'Aynı işlem şu anda sürüyor. Birkaç saniye sonra tekrar deneyin.',
  'Network.Unavailable': 'Sunucuya ulaşılamıyor. İnternet bağlantınızı kontrol edip tekrar deneyin.',
  'ParentApp.NotParent':
    'Bu uygulama velilere özeldir. Okul yöneticileri ve antrenörler yönetim panelinden giriş yapar.',
  'ParentApp.PasswordSetupRequired':
    'Devam etmek için e-postanızdaki davet bağlantısıyla şifrenizi belirleyin. Bağlantının süresi dolduysa okulunuzdan yeni bir davet isteyin.',
} satisfies Record<string, string>;

/** The shape of a language: the Turkish text with every literal widened to string (key parity at compile time). */
type Widen<T> = T extends string
  ? string
  : T extends readonly (infer U)[]
    ? readonly Widen<U>[]
    : { readonly [K in keyof T]: Widen<T[K]> };
export type ParentText = Widen<typeof trText>;
export type ErrorMessageText = Record<keyof typeof trErrorMessages, string>;

/** UI text in the active language (DEC-426). */
export const text: ParentText = localized<ParentText>({ tr: trText, en: enText });

/** Messages for the API's error codes in the active language; unknown codes are `undefined`. */
export const errorMessages: Readonly<Record<string, string>> = localized<Record<string, string>>({
  tr: trErrorMessages,
  en: enErrorMessages,
});

/** The raw dictionaries, for the key-parity tests. */
export const dictionaries = {
  tr: { text: trText, errors: trErrorMessages },
  en: { text: enText, errors: enErrorMessages },
} as const;

/** Fills `{name}` placeholders; unknown placeholders stay as they are. */
export type FormatArgs = Readonly<Record<string, string | number | null | undefined>>;

export function format(template: string, args: FormatArgs = {}): string {
  return template.replace(/\{(\w+)\}/g, (match, key: string) => {
    const value = args[key];
    return value === null || value === undefined ? match : String(value);
  });
}

/** Page title in the browser tab: "<page> · Spor Akademi". */
export function pageTitle(page: string): string {
  return `${page} · ${text.app.name}`;
}
