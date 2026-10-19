using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MessengerBackend;

public class DtoSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type.Name.EndsWith("In", StringComparison.Ordinal))
            schema.AdditionalPropertiesAllowed = false;
        var req = (schema.Required ??= new HashSet<string>());
        void Prop(string name, Action<OpenApiSchema> tune, bool required = true)
        {
            if (required) req.Add(name);
            if (schema.Properties.TryGetValue(name, out var s)) tune(s);
        }

        switch (context.Type.Name)
        {
            case "UpdateProfileIn":
                Prop("display_name", s => { s.MinLength = 1; s.MaxLength = 32; s.Nullable = true; }, false);
                Prop("bio", s => { s.MaxLength = 500; s.Nullable = true; }, false);
                break;
            case "RegisterIn":
            case "LoginIn":
                Prop("login", s => { s.MinLength = 3; s.MaxLength = 16; s.Pattern = "^[A-Za-z0-9_.\\-]{3,16}$"; });
                Prop("password", s => { s.MinLength = 8; s.MaxLength = 1024; });
                break;
            case "CreateChatIn":
                Prop("another_user_id", s => s.Minimum = 1);
                break;
            case "CreateGroupIn":
                Prop("title", s => { s.MinLength = 1; s.MaxLength = 64; });
                // дубли member_ids сервер дедуплицирует (описано в описании операции) — uniqueItems не заявляем
                Prop("member_ids", s => s.UniqueItems = null);
                break;
            case "AddMemberIn":
            case "TransferOwnerIn":
                Prop("user_id", s => s.Minimum = 1);
                break;
            case "SetRoleIn":
                Prop("role", s => s.Enum = [new OpenApiString("admin"), new OpenApiString("member")]);
                break;
            case "ReadIn":
                Prop("message_id", s => s.Minimum = 1);
                break;
            case "SendMessageIn":
                Prop("text", s => { s.MinLength = 1; s.MaxLength = 4096; s.Nullable = false; });
                Prop("reply_to_id", s => s.Minimum = 1, required: false);
                Prop("client_ref", s => { s.MaxLength = 64; }, required: false);
                break;
            case "EditMessageIn":
                Prop("text", s => { s.MinLength = 1; s.MaxLength = 4096; });
                break;
            case "DeleteMessageIn":
                Prop("for_everyone", s => s.Nullable = false);
                break;
            case "ReactionIn":
                Prop("emoji", s => { s.MinLength = 1; s.MaxLength = 32; });
                break;
            case "MessageOut":
                Prop("content_state", s => s.Enum =
                    [new OpenApiString("stored"), new OpenApiString("not_stored"), new OpenApiString("deleted")]);
                Prop("text", s => s.Nullable = true);
                break;
            case "MemberOut":
                Prop("role", s => s.Enum =
                    [new OpenApiString("member"), new OpenApiString("admin"), new OpenApiString("owner")]);
                break;
            case "DirectChatOut":
                Prop("type", s => s.Enum = [new OpenApiString("direct")]);
                break;
            case "GroupChatOut":
                Prop("type", s => s.Enum = [new OpenApiString("group")]);
                break;
        }

        foreach (var (name, prop) in schema.Properties)
            if ((name.EndsWith("_at") || name.EndsWith("_date")) && prop.Type == "string")
                prop.Format = "date-time";
    }

}
