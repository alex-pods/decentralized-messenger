namespace MessengerBackend;

public class MessageRow
{
    public long Id { get; set; }
    public long Chat_Id { get; set; }
    public long Sender_Id { get; set; }
    public long? Reply_To_Id { get; set; }
    private string _storedText = "";
    public int Storage_Version { get; set; }
    public string Text
    {
        get => Storage_Version == 1 ? Infrastructure.StorageEncryption.Unprotect(_storedText) : _storedText;
        set => _storedText = value;
    }
    public string Sent_At { get; set; } = "";
    public string? Delivered_At { get; set; }
    public string? Edited_At { get; set; }
    public string? Read_At { get; set; }
    public string? Deleted_At { get; set; }
    public string? Client_Ref { get; set; }
    public string? Client_Ref_Payload { get; set; }
}
