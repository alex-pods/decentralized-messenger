namespace MessengerBackend;

public class AttachmentRow
{
    public long Id { get; set; }
    public long Message_Id { get; set; }
    public string File_Name { get; set; } = "";
    public string Path_To_File { get; set; } = "";
    public string File_Type { get; set; } = "";
    public long File_Size_Bytes { get; set; }
}
