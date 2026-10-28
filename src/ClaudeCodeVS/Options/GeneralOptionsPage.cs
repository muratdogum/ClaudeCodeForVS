using System.ComponentModel;
using ClaudeCodeVS.Cli;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS.Options
{
    /// <summary>Tools > Options > Claude Code > Genel</summary>
    public sealed class GeneralOptionsPage : DialogPage
    {
        [Category("Claude Code CLI")]
        [DisplayName("claude.exe yolu")]
        [Description("Boş bırakılırsa PATH, %USERPROFILE%\\.local\\bin, npm ve VS Code eklentisi klasörlerinde otomatik aranır.")]
        public string ClaudeExecutablePath { get; set; } = string.Empty;

        [Category("Claude Code CLI")]
        [DisplayName("Model")]
        [Description("opus, sonnet, haiku, fable veya tam model kimliği. Boş bırakılırsa Claude Code'un varsayılan modeli kullanılır. Panelden de değiştirilebilir.")]
        public string Model { get; set; } = string.Empty;

        [Category("Claude Code CLI")]
        [DisplayName("İzin modu")]
        [Description("Default: her işlemde sorar. AcceptEdits: dosya düzenlemelerini otomatik kabul eder. Plan: önce plan hazırlar. " +
                     "Auto: Claude Code'un otomatik modu. BypassPermissions: hiçbir izin sormaz (dikkatli kullanın).")]
        public PermissionModeOption PermissionMode { get; set; } = PermissionModeOption.Default;

        [Category("Claude Code CLI")]
        [DisplayName("Ek argümanlar")]
        [Description("claude.exe'ye olduğu gibi eklenecek argümanlar. Örnek: --add-dir D:\\ortak")]
        public string AdditionalArguments { get; set; } = string.Empty;

        [Category("Davranış")]
        [DisplayName("Göndermeden önce dosyaları kaydet")]
        [Description("Claude'un güncel içeriği görmesi için mesaj göndermeden önce kaydedilmemiş açık dosyaları kaydeder.")]
        public bool SaveAllBeforeSend { get; set; } = true;

        [Category("Davranış")]
        [DisplayName("Aktif dosyayı bağlam olarak ekle")]
        [Description("Varsayılan olarak aktif dosyanın yolu ve seçili satırlar mesajla birlikte gönderilir.")]
        public bool IncludeActiveFile { get; set; } = true;

        [Category("Davranış")]
        [DisplayName("Visual Studio sistem istemi")]
        [Description("Claude'a Visual Studio içinde çalıştığını ve tıklanabilir dosya bağlantıları kullanmasını söyleyen kısa bir sistem istemi ekler.")]
        public bool AppendIdeSystemPrompt { get; set; } = true;

        [Category("Gizlilik ve güvenlik")]
        [DisplayName("Kurumsal gizlilik modu")]
        [Description("Telemetri/hata raporu/geri bildirim gönderimini kapatır; veri yayınlayan araçları, ağ komutlarını ve korunan dosyaların okunmasını engeller; " +
                     "'İzinleri atla' ve 'Otomatik' modlarını kaldırır. Kod yine de yanıt üretmek için seçili model sağlayıcısına gönderilir.")]
        public bool PrivacyMode { get; set; } = true;

        [Category("Gizlilik ve güvenlik")]
        [DisplayName("Web araçlarına izin ver")]
        [Description("Gizlilik modunda WebFetch/WebSearch varsayılan olarak engellidir (dış sitelere istek ve sorgu gönderirler).")]
        public bool AllowWebTools { get; set; }

        [Category("Gizlilik ve güvenlik")]
        [DisplayName("MCP sunucularına izin ver")]
        [Description("Gizlilik modunda kullanıcı/proje MCP sunucuları (Slack, Gmail, harici API'ler…) yüklenmez.")]
        public bool AllowMcpServers { get; set; }

        [Category("Gizlilik ve güvenlik")]
        [DisplayName("Beklenen kuruluş")]
        [Description("Şirket kuruluşunun adı veya kimliği (claude auth status'taki orgName/orgId). Doluysa ve Claude Code başka bir hesapla " +
                     "(ör. kişisel hesap) oturum açmışsa gizlilik modunda mesaj gönderilmez. Zorunlu kılmak için BT ekibi merkezi politikada forceLoginOrgUUID kullanmalıdır.")]
        public string ExpectedOrganization { get; set; } = string.Empty;

        [Category("Gizlilik ve güvenlik")]
        [DisplayName("Ağ komutlarını engelle")]
        [Description("curl, wget, Invoke-WebRequest, Invoke-RestMethod, scp gibi ağa veri taşıyan komutları (Bash ve PowerShell) onay verilse bile engeller. " +
                     "Kapalıyken de her komut onayınızı bekler ve ağ komutları kartta uyarıyla gösterilir.")]
        public bool BlockNetworkCommands { get; set; } = true;

        [Category("Gizlilik ve güvenlik")]
        [DisplayName("Korunan dosya desenleri")]
        [Description("Noktalı virgülle ayrılmış glob desenleri. Eşleşen dosyalar Claude tarafından okunamaz/düzenlenemez ve bağlam olarak eklenmez. " +
                     "Eğik çizgisiz desen her klasördeki dosya adına uyar (ör. .env, *.pfx); eğik çizgili desen yola uyar (ör. **/secrets/**).")]
        public string ProtectedFilePatterns { get; set; } = Privacy.PrivacyPolicy.DefaultProtectedPatterns;

        [Category("Gizlilik ve güvenlik")]
        [DisplayName("Gizli bilgi taraması")]
        [Description("Göndermeden önce mesajı ve eklenen kodu API anahtarı, parola, bağlantı dizesi, özel anahtar gibi değerler için tarar ve maskelemeyi önerir.")]
        public bool ScanForSecrets { get; set; } = true;

        [Category("Gizlilik ve güvenlik")]
        [DisplayName("Yerel gönderim günlüğü")]
        [Description("Modele gönderilen mesajları, eklenen dosyaları ve araçların eriştiği dosya/komutları yalnızca bu bilgisayarda %LOCALAPPDATA%\\ClaudeCodeVS\\audit altına kaydeder.")]
        public bool AuditLog { get; set; } = true;

        [Category("Model sağlayıcısı")]
        [DisplayName("Sağlayıcı")]
        [Description("Anthropic: doğrudan Anthropic. Bedrock/Vertex/Foundry: modeli şirketinizin kendi bulut hesabı üzerinden kullanır. " +
                     "CorporateGateway: ANTHROPIC_BASE_URL ile kurumsal bir ağ geçidine yönlendirir. Gerekli bölge/proje bilgileri 'Ortam değişkenleri'ne yazılır.")]
        public Privacy.ModelProvider Provider { get; set; } = Privacy.ModelProvider.Anthropic;

        [Category("Model sağlayıcısı")]
        [DisplayName("Ortam değişkenleri")]
        [Description("claude.exe'ye verilecek AD=DEĞER çiftleri, noktalı virgülle ayrılmış. Örnekler: AWS_REGION=eu-central-1; " +
                     "ANTHROPIC_VERTEX_PROJECT_ID=proje; CLOUD_ML_REGION=europe-west1; ANTHROPIC_FOUNDRY_RESOURCE=kaynak; ANTHROPIC_BASE_URL=https://llm-gateway.sirket.local")]
        public string EnvironmentVariables { get; set; } = string.Empty;
    }
}
