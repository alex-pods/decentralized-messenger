namespace MessengerBackend;

public class UserRow
{
    public long Id { get; set; }
    public string Tag { get; set; } = "";
    public string Password_Hash { get; set; } = "";
    public string Display_Name { get; set; } = "";
    public string? Path_To_Avatar_File { get; set; }
    private string? _storedBio;
    public int Storage_Version { get; set; }
    public string? Bio
    {
        get => Storage_Version == 1 && _storedBio is not null ? Infrastructure.StorageEncryption.Unprotect(_storedBio) : _storedBio;
        set => _storedBio = value;
    }
    public string Join_Date { get; set; } = "";
    public string? Last_Seen_At { get; set; }
}
