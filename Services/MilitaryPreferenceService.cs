using System.Text.Json.Nodes;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Mantém compatibilidade com os arquivos JSON usados pelo módulo Tkinter.
/// Os nomes das chaves e a ordem dos registros da lixeira permanecem idênticos,
/// permitindo alternar temporariamente entre a tela WPF e os fluxos legados.
/// </summary>
public sealed class MilitaryPreferenceService
{
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MilitaryPreferenceService(AppPaths paths, JsonFileService json)
    {
        _paths = paths;
        _json = json;
    }

    private string FavoritesPath => Path.Combine(_paths.DataDirectory, "favoritos.json");
    private string NotesPath => Path.Combine(_paths.DataDirectory, "anotacoes.json");
    private string AttachedPath => Path.Combine(_paths.DataDirectory, "adido_encostado.json");
    private string ColorsPath => Path.Combine(_paths.DataDirectory, "cores_custom.json");
    private string OrderPath => Path.Combine(_paths.DataDirectory, "ordem_custom.json");
    private string TrashPath => Path.Combine(_paths.DataDirectory, "lixeira.json");
    private string ListSettingsPath => Path.Combine(_paths.DataDirectory, "wpf_listar_militares.json");

    public async Task ApplyAsync(IList<MilitaryRecord> records)
    {
        var favorites = await LoadBooleanMapAsync(FavoritesPath);
        var attached = await LoadBooleanMapAsync(AttachedPath);
        var notes = await LoadStringMapAsync(NotesPath);
        var colors = await LoadStringMapAsync(ColorsPath);
        foreach (var record in records)
        {
            record.IsFavorite = favorites.TryGetValue(record.Id, out var fav) && fav;
            // Quando o JSON não possui o ID, preserva a coluna adido_encostado do SQLite.
            if (attached.TryGetValue(record.Id, out var add)) record.IsAttached = add;
            record.Annotation = notes.TryGetValue(record.Id, out var note) ? note : string.Empty;
            record.CustomColor = colors.TryGetValue(record.Id, out var color) ? color : string.Empty;
        }
    }

    public async Task ToggleFavoriteAsync(MilitaryRecord record)
    {
        record.IsFavorite = !record.IsFavorite;
        var map = await LoadBooleanMapAsync(FavoritesPath);
        if (record.IsFavorite) map[record.Id] = true; else map.Remove(record.Id);
        await SaveMapAsync(FavoritesPath, map.ToDictionary(x => x.Key.ToString(CultureInfo.InvariantCulture), x => x.Value));
    }

    public async Task SetAttachedAsync(MilitaryRecord record, bool value)
    {
        record.IsAttached = value;
        var map = await LoadBooleanMapAsync(AttachedPath);
        // Mantém o valor explícito, inclusive false, para o Python e o WPF concordarem.
        map[record.Id] = value;
        await SaveMapAsync(AttachedPath, map.ToDictionary(x => x.Key.ToString(CultureInfo.InvariantCulture), x => x.Value));
    }

    public async Task SetNoteAsync(MilitaryRecord record, string note)
    {
        record.Annotation = note?.Trim() ?? string.Empty;
        var map = await LoadStringMapAsync(NotesPath);
        if (string.IsNullOrWhiteSpace(record.Annotation)) map.Remove(record.Id); else map[record.Id] = record.Annotation;
        await SaveMapAsync(NotesPath, map.ToDictionary(x => x.Key.ToString(CultureInfo.InvariantCulture), x => x.Value));
    }

    public async Task SetColorAsync(MilitaryRecord record, string? color)
        => await SetColorsAsync([record], color);

    public async Task SetColorsAsync(IEnumerable<MilitaryRecord> records, string? color)
    {
        var selected = records.Where(x => x.Id > 0).DistinctBy(x => x.Id).ToList();
        if (selected.Count == 0) return;

        var normalizedColor = color?.Trim() ?? string.Empty;
        await _gate.WaitAsync();
        try
        {
            var map = await LoadStringMapAsync(ColorsPath);
            foreach (var record in selected)
            {
                record.CustomColor = normalizedColor;
                if (string.IsNullOrWhiteSpace(normalizedColor)) map.Remove(record.Id); else map[record.Id] = normalizedColor;
            }
            await SaveMapAsync(ColorsPath, map.ToDictionary(x => x.Key.ToString(CultureInfo.InvariantCulture), x => x.Value));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MilitaryListSettings> LoadListSettingsAsync()
        => await _json.LoadAsync<MilitaryListSettings>(ListSettingsPath) ?? new MilitaryListSettings();

    public async Task SaveListSettingsAsync(MilitaryListSettings settings)
        => await _json.SaveAsync(ListSettingsPath, settings);

    public async Task SaveCustomOrderAsync(IEnumerable<int> ids)
    {
        var payload = new OrderPayload { When = DateTime.Now, Order = ids.Distinct().ToList() };
        await _json.SaveAsync(OrderPath, payload);
    }

    public async Task<IReadOnlyList<int>> LoadCustomOrderAsync()
    {
        try
        {
            var payload = await _json.LoadAsync<OrderPayload>(OrderPath) ?? new OrderPayload();
            if (payload.Order.Count > 0) return payload.Order;
        }
        catch { }

        // Compatibilidade: algumas versões salvavam a ordem apenas dentro das preferências
        // do Listar Militares. O Auxílio-Transporte também precisa enxergar essa ordem.
        try
        {
            var settings = await LoadListSettingsAsync();
            if (settings.CustomOrder.Count > 0)
            {
                var migrated = settings.CustomOrder.Where(id => id > 0).Distinct().ToList();
                await SaveCustomOrderAsync(migrated);
                return migrated;
            }
        }
        catch { }

        return [];
    }

    public async Task AddToTrashAsync(MilitaryRecord record)
    {
        await _gate.WaitAsync();
        try
        {
            var node = await _json.LoadNodeAsync(TrashPath);
            var array = node as JsonArray ?? [];
            array.Add(new JsonObject
            {
                ["quando"] = DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
                ["registro"] = ToLegacyArray(record)
            });
            while (array.Count > 500) array.RemoveAt(0);
            await _json.SaveNodeAsync(TrashPath, array);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<MilitaryTrashEntry>> LoadTrashAsync()
    {
        var node = await _json.LoadNodeAsync(TrashPath);
        if (node is not JsonArray array) return [];
        var result = new List<MilitaryTrashEntry>();
        for (var index = 0; index < array.Count; index++)
        {
            try
            {
                if (array[index] is not JsonObject obj || obj["registro"] is not JsonArray row) continue;
                var whenText = obj["quando"]?.GetValue<string>() ?? string.Empty;
                DateTime.TryParse(whenText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var when);
                result.Add(new MilitaryTrashEntry { Index = index, DeletedAt = when, Record = FromLegacyArray(row) });
            }
            catch { }
        }
        return result;
    }

    public async Task RemoveTrashEntryAsync(int index)
    {
        await _gate.WaitAsync();
        try
        {
            var node = await _json.LoadNodeAsync(TrashPath);
            var array = node as JsonArray ?? [];
            if (index >= 0 && index < array.Count) array.RemoveAt(index);
            await _json.SaveNodeAsync(TrashPath, array);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearTrashAsync()
    {
        await _gate.WaitAsync();
        try { await _json.SaveNodeAsync(TrashPath, new JsonArray()); }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<int, bool>> LoadBooleanMapAsync(string path)
    {
        try
        {
            var raw = await _json.LoadAsync<Dictionary<string, bool>>(path) ?? [];
            return raw.Where(x => int.TryParse(x.Key, out _)).ToDictionary(x => int.Parse(x.Key, CultureInfo.InvariantCulture), x => x.Value);
        }
        catch { return []; }
    }

    private async Task<Dictionary<int, string>> LoadStringMapAsync(string path)
    {
        try
        {
            var raw = await _json.LoadAsync<Dictionary<string, string>>(path) ?? [];
            return raw.Where(x => int.TryParse(x.Key, out _)).ToDictionary(x => int.Parse(x.Key, CultureInfo.InvariantCulture), x => x.Value ?? string.Empty);
        }
        catch { return []; }
    }

    private async Task SaveMapAsync<T>(string path, T payload)
    {
        await _gate.WaitAsync();
        try { await _json.SaveAsync(path, payload); }
        finally { _gate.Release(); }
    }

    private static JsonArray ToLegacyArray(MilitaryRecord record)
    {
        object?[] values =
        [
            record.Id, record.Rank, record.Name, record.WarName, record.Cpf, record.PrecCp, record.MilitaryId,
            record.Bank, record.Agency, record.Account, record.PhotoPath, record.FormationYear, record.BirthDate,
            record.EnlistmentDate, record.Address, record.ZipCode, record.ReceivesPreSchool, record.PreSchoolValue,
            record.ReceivesTransportAid, record.TransportAidValue, record.HasPnr
        ];
        var array = new JsonArray();
        foreach (var value in values) array.Add(JsonValue.Create(value));
        return array;
    }

    private static MilitaryRecord FromLegacyArray(JsonArray row)
    {
        string Text(int index)
        {
            if (index < 0 || index >= row.Count || row[index] is null) return string.Empty;
            try { return row[index]!.GetValue<string>(); }
            catch { return row[index]!.ToJsonString().Trim('"'); }
        }
        int Number(int index)
        {
            if (index < 0 || index >= row.Count || row[index] is null) return 0;
            try { return row[index]!.GetValue<int>(); }
            catch { return int.TryParse(Text(index), out var value) ? value : 0; }
        }
        return new MilitaryRecord
        {
            Id = Number(0), Rank = Text(1), Name = Text(2), WarName = Text(3), Cpf = Text(4), PrecCp = Text(5),
            MilitaryId = Text(6), Bank = Text(7), Agency = Text(8), Account = Text(9), PhotoPath = Text(10),
            FormationYear = Text(11), BirthDate = Text(12), EnlistmentDate = Text(13), Address = Text(14),
            ZipCode = Text(15), ReceivesPreSchool = Text(16), PreSchoolValue = Text(17),
            ReceivesTransportAid = Text(18), TransportAidValue = Text(19), HasPnr = Text(20)
        };
    }

    private sealed class OrderPayload
    {
        [JsonPropertyName("quando")]
        public DateTime When { get; set; }

        [JsonPropertyName("ordem")]
        public List<int> Order { get; set; } = [];
    }
}
