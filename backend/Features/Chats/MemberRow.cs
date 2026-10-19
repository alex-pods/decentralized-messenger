namespace MessengerBackend;

public class MemberRow
{
    public long Chat_Id { get; set; }
    public long User_Id { get; set; }
    public string Role { get; set; } = "member";
    public string Joined_At { get; set; } = "";
    public long? Last_Read_Message_Id { get; set; }
    public string? Read_At { get; set; }
    public string? Tag { get; set; }
    public string? Display_Name { get; set; }
    public string? Path_To_Avatar_File { get; set; }
}
