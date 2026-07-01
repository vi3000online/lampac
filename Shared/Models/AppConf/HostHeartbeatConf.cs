namespace Shared.Models.AppConf;

// Пока процесс жив и обслуживает запросы, lampac раз в intervalSeconds POST-ит url,
// регистрируя себя как активный хост в реестре бэкенда (backend .../routes/plugin.ts).
// Публичный хост бэкенд выводит из IP запроса (nip.io), поэтому в пинге нет тела —
// достаточно самого факта запроса. Фронт забирает этот реестр и пробит его.
public class HostHeartbeatConf
{
    public bool enable { get; set; } = true;

    public string url { get; set; } = "https://api-m.vi3000.top/api/plugin/lampac/ping";

    public int intervalSeconds { get; set; } = 1;

    public int timeoutSeconds { get; set; } = 5;
}
