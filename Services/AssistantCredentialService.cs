using System.Runtime.InteropServices;

namespace SIGFUR.Wpf.Services;

/// <summary>Armazena a chave da API no Gerenciador de Credenciais do Windows.</summary>
public sealed class AssistantCredentialService
{
    private const string OpenAiTargetName = "SIGFUR/OpenAI/API";
    private const string DeepSeekTargetName = "SIGFUR/DeepSeek/API";
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;

    public bool HasApiKey(string provider = "OpenAI")
    {
        var key = ReadApiKey(provider);
        return !string.IsNullOrWhiteSpace(key);
    }

    public string? ReadApiKey(string provider = "OpenAI")
    {
        var targetName = TargetFor(provider);
        var environmentName = EnvironmentFor(provider);
        if (!OperatingSystem.IsWindows())
            return Environment.GetEnvironmentVariable(environmentName);

        if (!CredRead(targetName, CredTypeGeneric, 0, out var credentialPtr))
            return Environment.GetEnvironmentVariable(environmentName);

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPtr);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    public void SaveApiKey(string apiKey, string provider = "OpenAI")
    {
        apiKey = (apiKey ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Informe uma chave de API válida.");
        if (apiKey.Any(char.IsWhiteSpace) || apiKey.Length < 12)
            throw new InvalidOperationException("A chave informada não possui um formato válido.");

        var targetName = TargetFor(provider);
        var environmentName = EnvironmentFor(provider);

        if (!OperatingSystem.IsWindows())
        {
            Environment.SetEnvironmentVariable(environmentName, apiKey);
            return;
        }

        var bytes = Encoding.Unicode.GetBytes(apiKey);
        if (bytes.Length > 5120) throw new InvalidOperationException("A chave informada é maior que o limite do Windows Credential Manager.");

        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName,
                Comment = $"Chave da {DisplayName(provider)} usada pelo Assistente SIGFUR"
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Não foi possível salvar a chave no Gerenciador de Credenciais do Windows.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public void DeleteApiKey(string provider = "OpenAI")
    {
        var targetName = TargetFor(provider);
        var environmentName = EnvironmentFor(provider);
        if (!OperatingSystem.IsWindows())
        {
            Environment.SetEnvironmentVariable(environmentName, null);
            return;
        }
        if (!CredDelete(targetName, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1168) throw new Win32Exception(error, "Não foi possível remover a chave da API.");
        }
    }

    private static string TargetFor(string? provider) => IsDeepSeek(provider) ? DeepSeekTargetName : OpenAiTargetName;
    private static string EnvironmentFor(string? provider) => IsDeepSeek(provider) ? "DEEPSEEK_API_KEY" : "OPENAI_API_KEY";
    public static bool IsDeepSeek(string? provider) => string.Equals(provider, "DeepSeek", StringComparison.OrdinalIgnoreCase);
    public static string DisplayName(string? provider) => IsDeepSeek(provider) ? "DeepSeek" : "OpenAI";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite([In] ref NativeCredential userCredential, [In] uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
    private static extern void CredFree([In] IntPtr buffer);
}
