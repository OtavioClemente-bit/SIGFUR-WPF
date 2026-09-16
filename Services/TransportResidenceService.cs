using System.Text.Json;

namespace SIGFUR.Wpf.Services;

public sealed class TransportResidenceService(string dataDirectory)
{
    private string PreferencesPath => Path.Combine(dataDirectory, "auxilio_transporte", "declaracao_residencia.json");

    public string LoadDeclarationCity()
        => File.Exists(PreferencesPath) ? JsonSerializer.Deserialize<string>(File.ReadAllText(PreferencesPath)) ?? "" : "";

    public void SaveDeclarationCity(string city)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
        File.WriteAllText(PreferencesPath + ".tmp", JsonSerializer.Serialize(city.Trim()));
        File.Move(PreferencesPath + ".tmp", PreferencesPath, true);
    }

    private string Folder(int militaryId)
    {
        if (militaryId <= 0) throw new ArgumentOutOfRangeException(nameof(militaryId));
        return Path.Combine(dataDirectory, "auxilio_transporte", "residencia", militaryId.ToString());
    }

    public string Get(int militaryId)
    {
        var index = Path.Combine(Folder(militaryId), "anexo.json");
        if (!File.Exists(index)) return "";
        var name = JsonSerializer.Deserialize<string>(File.ReadAllText(index)) ?? "";
        if (name != Path.GetFileName(name)) throw new InvalidDataException("Caminho do comprovante inválido.");
        var path = Path.Combine(Folder(militaryId), name);
        if (!File.Exists(path)) throw new FileNotFoundException("O comprovante salvo não está disponível. Anexe novamente ou remova o vínculo.", path);
        return path;
    }

    public void Attach(int militaryId, string source)
    {
        var ext = Path.GetExtension(source).ToLowerInvariant();
        if (ext is not (".pdf" or ".png" or ".jpg" or ".jpeg"))
            throw new InvalidDataException("Use PDF, PNG ou JPG.");
        var folder = Folder(militaryId); Directory.CreateDirectory(folder);
        var name = "comprovante_" + Guid.NewGuid().ToString("N") + ext;
        File.Copy(source, Path.Combine(folder, name));
        var index = Path.Combine(folder, "anexo.json");
        File.WriteAllText(index + ".tmp", JsonSerializer.Serialize(name));
        File.Move(index + ".tmp", index, true);
    }

    public void Detach(int militaryId)
    {
        var index = Path.Combine(Folder(militaryId), "anexo.json");
        if (File.Exists(index)) File.Delete(index);
    }
}
