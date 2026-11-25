using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TILageMonitor;

/// <summary>Ein Zustand, wie ihn Home Assistant unter /api/states/&lt;entity_id&gt; erwartet.</summary>
public sealed record HomeAssistantEntity(string EntityId, string State, IReadOnlyDictionary<string, object?> Attributes)
{
    /// <summary>Teil hinter tilage_&lt;praxis&gt;_ – Grundlage fuer MQTT-Topics und unique_id.</summary>
    public string Key { get; init; } = "";

    /// <summary>Anzeigename ohne Praxis – unter MQTT setzt Home Assistant den Geraetenamen davor.</summary>
    public string Label { get; init; } = "";

    /// <summary>binary_sensor oder sensor.</summary>
    public string Component => EntityId[..EntityId.IndexOf('.')];
}

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
    DateTime Now)
{
    /// <summary>Rechnername fuer das Lebenszeichen – welcher PC in der Praxis meldet.</summary>
    public string? Machine { get; init; }

    /// <summary>Version der App fuer das Lebenszeichen.</summary>
    public string? AppVersion { get; init; }

    /// <summary>
    /// Letzter erfolgreicher Datenstand, auch wenn der Konnektor gerade nicht antwortet. Die
    /// Ablaufdaten bleiben gueltig – sie sollen bei einem Ausfall nicht verschwinden.
    /// </summary>
    public ConnectorSnapshot? LastKnownSnapshot { get; init; }
}

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
        // Ist nur GetCards gescheitert, weiss niemand, was steckt – dann "unbekannt" statt
        // "keine Karte". Sonst meldet Home Assistant eine gezogene SMC-B, die gar nicht fehlt.
        var cardsKnown = snapshot is not null && !snapshot.Warnings.Any(w => w.Contains("GetCards", StringComparison.Ordinal));

        string Id(string domain, string key) => $"{domain}.{Prefix}_{slug}_{key}";
        void Add(string domain, string key, string state, Dictionary<string, object?> attributes)
        {
            var label = attributes.TryGetValue("friendly_name", out var fn) && fn is string f && f.StartsWith(input.Name + " ", StringComparison.Ordinal)
                ? f[(input.Name.Length + 1)..]
                : key;
            list.Add(new HomeAssistantEntity(Id(domain, key), state, attributes) { Key = key, Label = label });
        }
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
        Add("binary_sensor", "erreichbar", input.Reachable ? "on" : "off",
            Attr("Konnektor erreichbar", "mdi:server-network",
                ("device_class", "connectivity"),
                ("fehler", input.Reachable ? null : input.LastError)));

        var worst = input.Alerts.Count == 0 ? (ToastUrgency?)null : input.Alerts.Max(a => a.Urgency);
        var status = !input.Reachable ? "nicht_erreichbar"
            : snapshot is null ? "abfrage_abgelehnt" // erreichbar, aber Zertifikat/Aufrufkontext abgelehnt
            : worst == ToastUrgency.Error ? "stoerung"
            : worst == ToastUrgency.Warning ? "warnung"
            : "ok";
        Add("sensor", "status", status,
            Attr("Status", "mdi:shield-check",
                ("hinweise", input.Alerts.Select(a => a.Title).ToArray()),
                ("fehler", snapshot is null ? input.LastError : null)));

        Add("binary_sensor", "vpn_ti", OnOff(snapshot?.Resource?.IsTiOnline),
            Attr("VPN TI", "mdi:vpn",
                ("device_class", "connectivity"),
                ("status", snapshot?.Resource?.VpnTiStatus),
                ("seit", Iso(snapshot?.Resource?.VpnTiTimestamp))));
        Add("binary_sensor", "vpn_sis", OnOff(snapshot?.Resource?.IsSisOnline),
            Attr("VPN SIS", "mdi:web",
                ("device_class", "connectivity"),
                ("status", snapshot?.Resource?.VpnSisStatus),
                ("seit", Iso(snapshot?.Resource?.VpnSisTimestamp))));
        Add("binary_sensor", "live_ereignisse", input.Reachable ? OnOff(input.LiveEvents) : "unknown",
            Attr("Live-Ereignisse", "mdi:bell-ring",
                ("device_class", "connectivity")));

        var activeErrors = snapshot?.Resource?.ActiveErrors.Select(e => e.Condition).ToArray() ?? [];
        Add("sensor", "betriebsfehler", snapshot is null ? "unknown" : activeErrors.Length.ToString(CultureInfo.InvariantCulture),
            Attr("Betriebsfehler", "mdi:alert-circle-outline",
                ("state_class", "measurement"),
                ("fehler", activeErrors)));

        Add("sensor", "karten", !cardsKnown ? "unknown" : snapshot!.Cards.Count.ToString(CultureInfo.InvariantCulture),
            Attr("Karten gesteckt", "mdi:card-account-details",
                ("state_class", "measurement"),
                ("karten", snapshot?.Cards.Select(c => c.DisplayName).ToArray())));
        var connected = snapshot?.Terminals.Count(t => t.Connected != false);
        Add("sensor", "terminals", snapshot is null ? "unknown" : connected!.Value.ToString(CultureInfo.InvariantCulture),
            Attr("Terminals verbunden", "mdi:credit-card-wireless",
                ("state_class", "measurement"),
                ("gesamt", snapshot?.Terminals.Count),
                ("getrennt", snapshot?.Terminals.Where(t => t.Connected == false).Select(t => string.IsNullOrWhiteSpace(t.Name) ? t.CtId : t.Name).ToArray())));

        var smcb = snapshot?.Cards.FirstOrDefault(c => c.IsSmcB);
        Add("binary_sensor", "smcb_gesteckt", !cardsKnown ? "unknown" : smcb is null ? "off" : "on",
            Attr("SMC-B gesteckt", "mdi:card-account-details-outline",
                ("device_class", "plug"),
                ("inhaber", smcb?.CardHolderName)));
        if (smcb is not null)
        {
            var pin = smcb.PinStates.FirstOrDefault();
            Add("sensor", "smcb_pin", pin is null || string.IsNullOrWhiteSpace(pin.Status) ? "unknown" : (pin.Error is not null ? "unbekannt" : pin.Status.ToLowerInvariant()),
                Attr("SMC-B PIN", "mdi:form-textbox-password",
                    ("verifiziert", pin?.IsVerified),
                    ("versuche_uebrig", pin?.LeftTries)));
        }

        // Lebenszeichen der App selbst: aendert sich bei jedem Versand. Bleibt es stehen, ist
        // der PC aus oder die App beendet – unabhaengig davon, ob der Konnektor laeuft.
        Add("sensor", "app_lebenszeichen", Iso(input.Now) ?? "unknown",
            Attr("App-Lebenszeichen", "mdi:heart-pulse",
                ("device_class", "timestamp"),
                ("rechner", input.Machine),
                ("version", input.AppVersion)));

        Add("sensor", "letzter_abruf", Iso(snapshot?.RetrievedAt ?? input.Now) ?? "unknown",
            Attr("Letzter Abruf", "mdi:clock-check-outline",
                ("device_class", "timestamp")));

        // --- Zertifikate: je Karte ein Sensor mit Resttagen -----------------------------
        // Ablaufdaten auch bei Ausfall aus dem letzten bekannten Stand – sie aendern sich nicht,
        // nur weil der Konnektor gerade schweigt.
        var certSource = cardsKnown ? snapshot : input.LastKnownSnapshot ?? snapshot;
        if (certSource is not null)
        {
            var snapshotForCerts = certSource;
            var subjects = ConnectorCertificateExpiry.Collect(snapshotForCerts, input.ClientCertificateSubject, input.ClientCertificateNotAfter);
            foreach (var subject in subjects)
            {
                var card = snapshotForCerts.Cards.FirstOrDefault(c => subject.Key.EndsWith(":" + (string.IsNullOrWhiteSpace(c.Iccsn) ? c.CardHandle : c.Iccsn), StringComparison.Ordinal));
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
                    : $"{kindLabel} {Holder(card, snapshotForCerts)} Ablauf".Replace("  ", " ");
                Add("sensor", $"{kindKey}{suffix}_ablauf", days.ToString(CultureInfo.InvariantCulture),
                    Attr(label, subject.Kind == ConnectorCertificateKind.ClientSystem ? "mdi:certificate" : "mdi:card-bulleted-outline",
                        ("unit_of_measurement", "d"),
                        ("state_class", "measurement"),
                        ("gueltig_bis", subject.NotAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                        ("warnstufe", ConnectorCertificateExpiry.StageFor(days)),
                        ("kartentyp", kindLabel),
                        ("inhaber", card?.CardHolderName is { Length: > 0 } h ? h : null),
                        ("terminal", card is null ? null : TerminalName(card, snapshotForCerts)),
                        ("slot", card?.SlotId),
                        ("iccsn", card?.Iccsn)));
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

    /// <summary>
    /// Sendet alle Zustaende – je nach Einstellung per REST-API oder MQTT. Nicht mehr gemeldete
    /// Karten werden entfernt (MQTT) bzw. auf "unavailable" gesetzt (REST).
    /// </summary>
    public static async Task PublishAsync(ConnectorSettings settings, HomeAssistantInput input, CancellationToken ct)
    {
        if (!settings.HomeAssistantEnabled)
        {
            await HomeAssistantMqtt.StopAsync();
            return;
        }
        if (settings.HomeAssistantUsesMqtt ? string.IsNullOrWhiteSpace(settings.MqttHost) : string.IsNullOrWhiteSpace(settings.HomeAssistantUrl))
            return;
        if (!await Gate.WaitAsync(0, ct))
            return; // vorheriger Versand haengt noch – nicht stapeln
        try
        {
            if (settings.HomeAssistantUsesMqtt)
            {
                LastCount = await HomeAssistantMqtt.PublishAsync(settings, input, ct);
                LastSuccess = DateTime.Now;
                LastError = null;
                return;
            }
            await HomeAssistantMqtt.StopAsync();

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
            LastError = ex is TaskCanceledException or OperationCanceledException
                ? LocalizationService.Pick("Zeitüberschreitung – Home Assistant antwortet nicht.", "Timeout – Home Assistant does not respond.")
                : ex.Message;
        }
        finally
        {
            Gate.Release();
            Updated?.Invoke();
        }
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

/// <summary>
/// MQTT-Weg mit Home-Assistant-Discovery: jede Praxis erscheint als eigenes Geraet, die
/// Sensoren haben feste unique_ids und bleiben ueber Neustarts von Home Assistant erhalten.
/// Der Last Will meldet "offline", sobald die App ohne Abmeldung verschwindet (PC aus).
/// </summary>
public static class HomeAssistantMqtt
{
    public const string DiscoveryPrefix = "homeassistant";

    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static MqttConnection? _connection;
    private static string? _connectionKey;
    private static string? _slug;
    private static readonly Dictionary<string, string> SentConfigs = new(StringComparer.Ordinal);

    public static string BaseTopic(string slug) => $"{HomeAssistantStates.Prefix}/{slug}";
    public static string AvailabilityTopic(string slug) => $"{BaseTopic(slug)}/availability";

    /// <summary>Eine Entitaet als Discovery-Konfiguration samt State- und Attribut-Topic (I/O-frei).</summary>
    public sealed record Message(string ConfigTopic, string ConfigJson, string StateTopic, string StatePayload, string AttributesTopic, string AttributesJson);

    private static readonly HashSet<string> DiscoveryKeys = new(StringComparer.Ordinal)
    {
        "device_class", "unit_of_measurement", "state_class", "icon"
    };

    public static Message Build(HomeAssistantEntity entity, string slug, string deviceName, ConnectorServiceDirectory? directory, int expireAfterSeconds)
    {
        var baseTopic = BaseTopic(slug);
        var objectId = $"{HomeAssistantStates.Prefix}_{slug}_{entity.Key}";
        var config = new JsonObject
        {
            ["name"] = entity.Label,
            ["unique_id"] = objectId,
            ["object_id"] = objectId,
            ["default_entity_id"] = $"{entity.Component}.{objectId}",
            ["state_topic"] = $"{baseTopic}/{entity.Key}/state",
            ["json_attributes_topic"] = $"{baseTopic}/{entity.Key}/attributes",
            ["availability_topic"] = AvailabilityTopic(slug),
            ["payload_available"] = "online",
            ["payload_not_available"] = "offline",
            ["expire_after"] = expireAfterSeconds,
            ["device"] = new JsonObject
            {
                ["identifiers"] = new JsonArray($"{HomeAssistantStates.Prefix}_{slug}"),
                ["name"] = deviceName,
                ["manufacturer"] = string.IsNullOrWhiteSpace(directory?.ProductVendorName) ? "TI-Lage Monitor" : directory!.ProductVendorName,
                ["model"] = string.IsNullOrWhiteSpace(directory?.ProductName) ? "Konnektor" : directory!.ProductName,
                ["sw_version"] = string.IsNullOrWhiteSpace(directory?.FirmwareVersion) ? null : directory!.FirmwareVersion
            },
            ["origin"] = new JsonObject { ["name"] = "TI-Lage Monitor" }
        };
        if (entity.Component == "binary_sensor")
        {
            config["payload_on"] = "on";
            config["payload_off"] = "off";
        }
        if (entity.Key == "app_lebenszeichen")
            config.Remove("expire_after"); // das Lebenszeichen selbst soll sichtbar altern

        var attributes = new JsonObject();
        foreach (var (key, value) in entity.Attributes)
        {
            if (value is null || key is "friendly_name")
                continue;
            if (DiscoveryKeys.Contains(key))
                config[key] = JsonSerializer.SerializeToNode(value, value.GetType());
            else
                attributes[key] = JsonSerializer.SerializeToNode(value, value.GetType());
        }

        // "None" setzt einen MQTT-Sensor in Home Assistant auf "unbekannt".
        var state = entity.State is "unknown" or "unavailable" ? "None" : entity.State;
        return new Message(
            $"{DiscoveryPrefix}/{entity.Component}/{objectId}/config",
            config.ToJsonString(),
            $"{baseTopic}/{entity.Key}/state",
            state,
            $"{baseTopic}/{entity.Key}/attributes",
            attributes.ToJsonString());
    }

    /// <summary>Sendet alle Entitaeten; gibt die Anzahl zurueck.</summary>
    public static async Task<int> PublishAsync(ConnectorSettings settings, HomeAssistantInput input, CancellationToken ct)
    {
        await Lock.WaitAsync(ct);
        try
        {
            var slug = HomeAssistantStates.Slug(input.Name);
            var key = string.Join("\u001f", settings.MqttHost.Trim(), settings.MqttPort, settings.MqttUseTls,
                settings.MqttUser.Trim(), settings.MqttPasswordProtected ?? "", slug);
            if (_connection is null || !_connection.IsConnected || key != _connectionKey)
            {
                await CloseAsync(sendOffline: key != _connectionKey);
                var options = new MqttOptions(
                    settings.MqttHost.Trim(),
                    settings.MqttPort is > 0 and < 65536 ? settings.MqttPort : (settings.MqttUseTls ? 8883 : 1883),
                    settings.MqttUseTls,
                    string.IsNullOrWhiteSpace(settings.MqttUser) ? null : settings.MqttUser.Trim(),
                    SecretProtector.Unprotect(settings.MqttPasswordProtected),
                    $"tilage-{slug}-{Environment.MachineName}".ToLowerInvariant(),
                    AvailabilityTopic(slug),
                    "offline",
                    TimeSpan.FromSeconds(60));
                var connection = new MqttConnection(options);
                try
                {
                    await connection.ConnectAsync(ct);
                }
                catch
                {
                    await connection.DisposeAsync();
                    throw;
                }
                _connection = connection;
                _connectionKey = key;
                _slug = slug;
                SentConfigs.Clear();
            }

            var expireAfter = Math.Max(900, settings.PollMinutes * 60 * 3);
            var entities = HomeAssistantStates.Build(input);
            var current = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entity in entities)
            {
                var message = Build(entity, slug, input.Name, input.Snapshot?.Directory, expireAfter);
                current.Add(message.ConfigTopic);
                if (!SentConfigs.TryGetValue(message.ConfigTopic, out var sent) || sent != message.ConfigJson)
                {
                    await _connection.PublishAsync(message.ConfigTopic, message.ConfigJson, retain: true, ct);
                    SentConfigs[message.ConfigTopic] = message.ConfigJson;
                }
                await _connection.PublishAsync(message.AttributesTopic, message.AttributesJson, retain: true, ct);
                await _connection.PublishAsync(message.StateTopic, message.StatePayload, retain: true, ct);
            }

            // Gezogene Karten: leere Konfiguration loescht die Entitaet in Home Assistant.
            // Nur wenn der Konnektor geantwortet hat – sonst verschwaenden alle Karten bei jedem Ausfall.
            var cardsKnown = input.Reachable && input.Snapshot is not null
                && !input.Snapshot.Warnings.Any(w => w.Contains("GetCards", StringComparison.Ordinal));
            if (cardsKnown)
            {
                foreach (var gone in SentConfigs.Keys.Where(t => !current.Contains(t)).ToList())
                {
                    await _connection.PublishAsync(gone, "", retain: true, ct);
                    SentConfigs.Remove(gone);
                }
            }

            await _connection.PublishAsync(AvailabilityTopic(slug), "online", retain: true, ct);
            return entities.Count;
        }
        catch
        {
            // Beim naechsten Versand neu verbinden.
            _connection?.Abort();
            _connection = null;
            throw;
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>Abmelden (Home Assistant ausgeschaltet oder auf REST umgestellt).</summary>
    public static async Task StopAsync()
    {
        if (_connection is null)
            return;
        await Lock.WaitAsync();
        try
        {
            await CloseAsync(sendOffline: true);
        }
        finally
        {
            Lock.Release();
        }
    }

    private static async Task CloseAsync(bool sendOffline)
    {
        var connection = _connection;
        _connection = null;
        _connectionKey = null;
        if (connection is null)
            return;
        if (sendOffline && _slug is not null && connection.IsConnected)
        {
            try { await connection.PublishAsync(AvailabilityTopic(_slug), "offline", retain: true, CancellationToken.None); }
            catch { /* egal – der Broker sieht die Trennung */ }
        }
        await connection.DisposeAsync();
    }

    /// <summary>Verbindung testen: anmelden und wieder sauber abmelden.</summary>
    public static async Task<string?> TestAsync(string host, int port, bool tls, string user, string password, CancellationToken ct)
    {
        try
        {
            var connection = new MqttConnection(new MqttOptions(host.Trim(), port is > 0 and < 65536 ? port : (tls ? 8883 : 1883), tls,
                string.IsNullOrWhiteSpace(user) ? null : user.Trim(), password,
                $"tilage-test-{Guid.NewGuid():N}"[..23], "tilage/test/availability", "offline", TimeSpan.FromSeconds(30)));
            await connection.ConnectAsync(ct);
            await connection.DisposeAsync();
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return LocalizationService.Pick("Zeitüberschreitung – MQTT-Broker antwortet nicht.", "Timeout – MQTT broker does not respond.");
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
