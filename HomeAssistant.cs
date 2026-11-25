using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TILageMonitor;

/// <summary>Ein Zustand, wie ihn Home Assistant unter /api/states/&lt;entity_id&gt; erwartet.</summary>
public sealed record HomeAssistantEntity(string EntityId, string State, IReadOnlyDictionary<string, object?> Attributes);

/// <summary>Alles, was in einen Sendevorgang eingeht – ohne I/O, damit die Zuordnung testbar bleibt.</summary>
public sealed record HomeAssistantInput(
    string Name,
    bool Reachable,
    ConnectorSnapshot? Snapshot,
    IReadOnlyList<ConnectorAlert> Alerts,
    string? ClientCertificateSubject,
    DateTime? ClientCertificateNotAfter,
    bool? LiveEvents,
    string? LastError,
    DateTime Now);

/// <summary>
/// Leitet aus einem Abruf die Sensoren fuer Home Assistant ab. Jede Praxis bekommt ein eigenes
/// Kuerzel im Entitaetsnamen (sensor.tilage_&lt;praxis&gt;_…), so lassen sich beliebig viele
/// Konnektoren nebeneinander in einer Home-Assistant-Instanz ueberwachen.
/// </summary>
public static class HomeAssistantStates
{
    public const string Prefix = "tilage";

    public static List<HomeAssistantEntity> Build(HomeAssistantInput input)
    {
        var slug = Slug(input.Name);
        var list = new List<HomeAssistantEntity>();
        var snapshot = input.Reachable ? input.Snapshot : null;

        string Id(string domain, string key) => $"{domain}.{Prefix}_{slug}_{key}";
        Dictionary<string, object?> Attr(string label, string icon, params (string Key, object? Value)[] extra)
        {
            var d = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["friendly_name"] = $"{input.Name} {label}",
                ["icon"] = icon,
                ["praxis"] = input.Name,
                ["quelle"] = "TI-Lage Monitor"
            };
            foreach (var (k, v) in extra)
                d[k] = v;
            return d;
        }
        static string OnOff(bool? value) => value switch { true => "on", false => "off", null => "unknown" };

        // --- Konnektor als Ganzes -------------------------------------------------------
        list.Add(new(Id("binary_sensor", "erreichbar"), input.Reachable ? "on" : "off",
            Attr("Konnektor erreichbar", "mdi:server-network",
                ("device_class", "connectivity"),
                ("fehler", input.Reachable ? null : input.LastError))));

        var worst = input.Alerts.Count == 0 ? (ToastUrgency?)null : input.Alerts.Max(a => a.Urgency);
        var status = !input.Reachable ? "nicht_erreichbar"
            : worst == ToastUrgency.Error ? "stoerung"
            : worst == ToastUrgency.Warning ? "warnung"
            : "ok";
        list.Add(new(Id("sensor", "status"), status,
            Attr("Status", "mdi:shield-check",
                ("hinweise", input.Alerts.Select(a => a.Title).ToArray()))));

        list.Add(new(Id("binary_sensor", "vpn_ti"), OnOff(snapshot?.Resource?.IsTiOnline),
            Attr("VPN TI", "mdi:vpn",
                ("device_class", "connectivity"),
                ("status", snapshot?.Resource?.VpnTiStatus),
                ("seit", Iso(snapshot?.Resource?.VpnTiTimestamp)))));
        list.Add(new(Id("binary_sensor", "vpn_sis"), OnOff(snapshot?.Resource?.IsSisOnline),
            Attr("VPN SIS", "mdi:web",
                ("device_class", "connectivity"),
                ("status", snapshot?.Resource?.VpnSisStatus),
                ("seit", Iso(snapshot?.Resource?.VpnSisTimestamp)))));
        list.Add(new(Id("binary_sensor", "live_ereignisse"), input.Reachable ? OnOff(input.LiveEvents) : "unknown",
            Attr("Live-Ereignisse", "mdi:bell-ring",
                ("device_class", "connectivity"))));

        var activeErrors = snapshot?.Resource?.ActiveErrors.Select(e => e.Condition).ToArray() ?? [];
        list.Add(new(Id("sensor", "betriebsfehler"), snapshot is null ? "unknown" : activeErrors.Length.ToString(CultureInfo.InvariantCulture),
            Attr("Betriebsfehler", "mdi:alert-circle-outline",
                ("state_class", "measurement"),
                ("fehler", activeErrors))));

        list.Add(new(Id("sensor", "karten"), snapshot is null ? "unknown" : snapshot.Cards.Count.ToString(CultureInfo.InvariantCulture),
            Attr("Karten gesteckt", "mdi:card-account-details",
                ("state_class", "measurement"),
                ("karten", snapshot?.Cards.Select(c => c.DisplayName).ToArray()))));
        var connected = snapshot?.Terminals.Count(t => t.Connected != false);
        list.Add(new(Id("sensor", "terminals"), snapshot is null ? "unknown" : connected!.Value.ToString(CultureInfo.InvariantCulture),
            Attr("Terminals verbunden", "mdi:credit-card-wireless",
                ("state_class", "measurement"),
                ("gesamt", snapshot?.Terminals.Count),
                ("getrennt", snapshot?.Terminals.Where(t => t.Connected == false).Select(t => string.IsNullOrWhiteSpace(t.Name) ? t.CtId : t.Name).ToArray()))));

        var smcb = snapshot?.Cards.FirstOrDefault(c => c.IsSmcB);
        list.Add(new(Id("binary_sensor", "smcb_gesteckt"), snapshot is null ? "unknown" : smcb is null ? "off" : "on",
            Attr("SMC-B gesteckt", "mdi:card-account-details-outline",
                ("device_class", "plug"),
                ("inhaber", smcb?.CardHolderName))));
        if (smcb is not null)
        {
            var pin = smcb.PinStates.FirstOrDefault();
            list.Add(new(Id("sensor", "smcb_pin"), pin is null ? "unknown" : (pin.Error is not null ? "unbekannt" : pin.Status.ToLowerInvariant()),
                Attr("SMC-B PIN", "mdi:form-textbox-password",
                    ("verifiziert", pin?.IsVerified),
                    ("versuche_uebrig", pin?.LeftTries))));
        }

        list.Add(new(Id("sensor", "letzter_abruf"), Iso(snapshot?.RetrievedAt ?? input.Now) ?? "unknown",
            Attr("Letzter Abruf", "mdi:clock-check-outline",
                ("device_class", "timestamp"))));

        // --- Zertifikate: je Karte ein Sensor mit Resttagen -----------------------------
        if (snapshot is not null)
        {
            var subjects = ConnectorCertificateExpiry.Collect(snapshot, input.ClientCertificateSubject, input.ClientCertificateNotAfter);
            foreach (var subject in subjects)
            {
                var card = snapshot.Cards.FirstOrDefault(c => subject.Key.EndsWith(":" + (string.IsNullOrWhiteSpace(c.Iccsn) ? c.CardHandle : c.Iccsn), StringComparison.Ordinal));
                var kindKey = subject.Kind switch
                {
                    ConnectorCertificateKind.SmcB => "smcb",
                    ConnectorCertificateKind.Hba => "hba",
                    ConnectorCertificateKind.SmcKt => "smckt",
                    _ => "clientsystem"
                };
                var kindLabel = subject.Kind switch
                {
                    ConnectorCertificateKind.SmcB => "SMC-B",
                    ConnectorCertificateKind.Hba => "HBA",
                    ConnectorCertificateKind.SmcKt => "gSMC-KT",
                    _ => "Clientsystem-Zertifikat"
                };
                var suffix = card is null ? "" : "_" + CardSuffix(card);
                var days = ConnectorCertificateExpiry.DaysLeft(subject.NotAfter, input.Now);
                var label = subject.Kind == ConnectorCertificateKind.ClientSystem
                    ? $"{kindLabel} Ablauf"
                    : $"{kindLabel} {Holder(card, snapshot)} Ablauf".Replace("  ", " ");
                list.Add(new(Id("sensor", $"{kindKey}{suffix}_ablauf"), days.ToString(CultureInfo.InvariantCulture),
                    Attr(label, subject.Kind == ConnectorCertificateKind.ClientSystem ? "mdi:certificate" : "mdi:card-bulleted-outline",
                        ("unit_of_measurement", "d"),
                        ("state_class", "measurement"),
                        ("gueltig_bis", subject.NotAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                        ("warnstufe", ConnectorCertificateExpiry.StageFor(days)),
                        ("kartentyp", kindLabel),
                        ("inhaber", card?.CardHolderName is { Length: > 0 } h ? h : null),
                        ("terminal", card is null ? null : TerminalName(card, snapshot)),
                        ("slot", card?.SlotId),
                        ("iccsn", card?.Iccsn))));
            }
        }
        return list;
    }

    /// <summary>Entitaetskuerzel aus dem Praxisnamen: klein, ASCII, Unterstriche.</summary>
    public static string Slug(string? name)
    {
        var s = (name ?? "").Trim().ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
                sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '_')
                sb.Append('_');
        }
        var slug = sb.ToString().Trim('_');
        if (slug.Length > 40)
            slug = slug[..40].TrimEnd('_');
        return slug.Length == 0 ? "konnektor" : slug;
    }

    private static string CardSuffix(ConnectorCard card)
    {
        var id = string.IsNullOrWhiteSpace(card.Iccsn) ? card.CardHandle : card.Iccsn;
        var digits = new string(id.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return digits.Length <= 6 ? (digits.Length == 0 ? "x" : digits) : digits[^6..];
    }

    private static string Holder(ConnectorCard? card, ConnectorSnapshot snapshot)
    {
        if (card is null)
            return "";
        if (card.IsSmcKt)
            return TerminalName(card, snapshot);
        return card.CardHolderName;
    }

    private static string TerminalName(ConnectorCard card, ConnectorSnapshot snapshot)
    {
        var ct = snapshot.Terminals.FirstOrDefault(t => string.Equals(t.CtId, card.CtId, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(ct?.Name) ? card.CtId : ct!.Name;
    }

    private static string? Iso(DateTime? value) =>
        value is { } v && v != default
            ? new DateTimeOffset(v.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v, DateTimeKind.Local)).ToString("o", CultureInfo.InvariantCulture)
            : null;
}

/// <summary>
/// Schickt die Sensoren an Home Assistant (REST-API, Long-Lived Access Token). Fehler werden
/// nur gemerkt und im Konnektor-Fenster angezeigt – die Ueberwachung selbst laeuft weiter.
/// </summary>
public static class HomeAssistantPublisher
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HashSet<string> Published = new(StringComparer.Ordinal);

    public static DateTime? LastSuccess { get; private set; }
    public static string? LastError { get; private set; }
    public static int LastCount { get; private set; }

    /// <summary>Wird nach jedem Sendeversuch ausgelöst (Thread-Pool).</summary>
    public static event Action? Updated;

    public static Uri BuildStateUri(string baseUrl, string entityId)
    {
        var trimmed = (baseUrl ?? "").Trim().TrimEnd('/');
        if (trimmed.Length == 0)
            throw new ArgumentException(LocalizationService.Pick("Keine Home-Assistant-Adresse angegeben.", "No Home Assistant address given."));
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "http://" + trimmed;
        if (trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];
        if (!Uri.TryCreate(trimmed + "/api/states/" + entityId, UriKind.Absolute, out var uri))
            throw new ArgumentException(LocalizationService.Pick($"Ungültige Home-Assistant-Adresse: {baseUrl}", $"Invalid Home Assistant address: {baseUrl}"));
        return uri;
    }

    public static string ToJson(HomeAssistantEntity entity)
    {
        var attributes = new JsonObject();
        foreach (var (key, value) in entity.Attributes)
        {
            if (value is null)
                continue;
            attributes[key] = JsonSerializer.SerializeToNode(value, value.GetType());
        }
        return new JsonObject { ["state"] = entity.State, ["attributes"] = attributes }.ToJsonString();
    }

    /// <summary>Sendet alle Zustaende. Nicht mehr gemeldete Karten werden auf "unavailable" gesetzt.</summary>
    public static async Task PublishAsync(ConnectorSettings settings, HomeAssistantInput input, CancellationToken ct)
    {
        if (!settings.HomeAssistantEnabled || string.IsNullOrWhiteSpace(settings.HomeAssistantUrl))
            return;
        if (!await Gate.WaitAsync(0, ct))
            return; // vorheriger Versand haengt noch – nicht stapeln
        try
        {
            var token = SecretProtector.Unprotect(settings.HomeAssistantTokenProtected);
            var entities = HomeAssistantStates.Build(input);
            var current = entities.Select(e => e.EntityId).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in Published.Where(id => !current.Contains(id)).ToList())
            {
                entities.Add(new HomeAssistantEntity(gone, "unavailable", new Dictionary<string, object?>()));
                Published.Remove(gone);
            }

            foreach (var entity in entities)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, BuildStateUri(settings.HomeAssistantUrl, entity.EntityId))
                {
                    Content = new StringContent(ToJson(entity), Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await Http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(DescribeStatus((int)response.StatusCode));
                if (entity.State != "unavailable")
                    Published.Add(entity.EntityId);
            }

            LastSuccess = DateTime.Now;
            LastError = null;
            LastCount = current.Count;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LastError = ex is TaskCanceledException
                ? LocalizationService.Pick("Zeitüberschreitung – Home Assistant antwortet nicht.", "Timeout – Home Assistant does not respond.")
                : ex.Message;
        }
        finally
        {
            Gate.Release();
        }
        Updated?.Invoke();
    }

    /// <summary>Prueft Adresse und Token mit GET /api/ (liefert "API running.").</summary>
    public static async Task<string?> TestAsync(string url, string token, CancellationToken ct)
    {
        try
        {
            var uri = BuildStateUri(url, "x");
            var apiRoot = new Uri(uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath[..(uri.AbsolutePath.IndexOf("/api/", StringComparison.Ordinal) + 5)]);
            using var request = new HttpRequestMessage(HttpMethod.Get, apiRoot);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? null : DescribeStatus((int)response.StatusCode);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return LocalizationService.Pick("Zeitüberschreitung – Home Assistant antwortet nicht.", "Timeout – Home Assistant does not respond.");
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string DescribeStatus(int code) => code switch
    {
        401 => LocalizationService.Pick("Home Assistant: Token ungültig (401).", "Home Assistant: invalid token (401)."),
        403 => LocalizationService.Pick("Home Assistant: Zugriff verweigert (403).", "Home Assistant: access denied (403)."),
        404 => LocalizationService.Pick("Home Assistant: API nicht gefunden (404) – Adresse prüfen.", "Home Assistant: API not found (404) – check the address."),
        _ => LocalizationService.Pick($"Home Assistant antwortet mit HTTP {code}.", $"Home Assistant responded with HTTP {code}.")
    };
}
