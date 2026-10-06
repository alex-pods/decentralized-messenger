using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MessengerBackend;

/// <summary>
/// Приводит swagger.json к реальному контракту: security на защищённых ручках
/// (Bearer ИЛИ X-Session-Token как альтернативы), ErrorResponse на кодах ошибок,
/// multipart-тела для файловых POST, бинарные ответы для скачивания.
/// </summary>
public class AuthAndErrorsOperationFilter : IOperationFilter
{
    private static readonly string[] PublicPrefixes = ["/auth/register", "/auth/login", "/health", "/ws/protocol"];

    private static readonly (string Prefix, string? Method, string[] Codes)[] ErrorMap =
    [
        ("/auth/register", "POST", ["400", "409", "429"]),
        ("/auth/login", "POST", ["400", "401", "429"]),
        ("/auth/logout", "POST", ["401"]),
        ("/users/me/settings", "PATCH", ["400", "401"]),
        ("/users/me/settings", "GET", ["401"]),
        ("/users/me/avatar", "POST", ["400", "401", "413"]),
        ("/users/me/avatar", "GET", ["401", "404"]),
        ("/users/", "GET", ["401", "404"]),
        ("/users/me", "PATCH", ["400", "401"]),
        ("/users/me", "GET", ["401"]),
        ("/users/search", "GET", ["400", "401"]),
        ("/chats/group", "POST", ["400", "401", "404"]),
        ("/chats/{chat_id}/members/{user_id}", "DELETE", ["400", "401", "403", "404"]),
        ("/chats/{chat_id}/members/{user_id}", "PATCH", ["400", "401", "403", "404"]),
        ("/chats/{chat_id}/owner", "POST", ["400", "401", "403", "404"]),
        ("/chats/{chat_id}/members", "POST", ["400", "401", "403", "404"]),
        ("/chats/{chat_id}/members", "GET", ["401", "403", "404"]),
        ("/chats/{chat_id}/messages", "GET", ["400", "401", "403", "404"]),
        ("/chats/{chat_id}/messages", "POST", ["400", "401", "403", "404"]),
        ("/chats/{chat_id}/read", "POST", ["400", "401", "403", "404"]),
        ("/chats/{chat_id}/leave", "POST", ["400", "401", "403", "404", "409"]),
        ("/chats/{chat_id}", "GET", ["401", "403", "404"]),
        ("/chats/{chat_id}", "DELETE", ["401", "403", "404"]),
        ("/chats", "GET", ["401"]),
        ("/chats", "POST", ["400", "401", "404"]),
        ("/messages/{message_id}/reactions", "POST", ["400", "401", "403", "404", "409"]),
        ("/messages/{message_id}/reactions", "DELETE", ["401", "403", "404"]),
        ("/messages/{message_id}/attachments", "POST", ["400", "401", "403", "404", "409", "413"]),
        ("/messages/{message_id}", "PATCH", ["400", "401", "403", "404", "409"]),
        ("/messages/{message_id}", "DELETE", ["400", "401", "403", "404"]),
        ("/attachments/", "GET", ["401", "403", "404"]),
    ];

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var path = System.Text.RegularExpressions.Regex.Replace(
            "/" + (context.ApiDescription.RelativePath ?? "").TrimStart('/'),
            @"\{(\w+):[^}]+\}", "{$1}");
        var method = context.ApiDescription.HttpMethod?.ToUpperInvariant();
        var isPublic = PublicPrefixes.Any(path.StartsWith);

        // security: защищённые ручки — Bearer ИЛИ X-Session-Token (альтернативы); публичные — без security
        if (isPublic)
        {
            operation.Security = [];
        }
        else
        {
            operation.Security =
            [
                new OpenApiSecurityRequirement
                {
                    [new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "SessionBearer" }
                    }] = []
                },
                new OpenApiSecurityRequirement
                {
                    [new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "SessionHeader" }
                    }] = []
                },
            ];
        }

        // multipart-тела для файловых POST
        if (method == "POST" && (path == "/users/me/avatar" || path.StartsWith("/messages/{message_id}/attachments")))
        {
            var multiple = path.StartsWith("/messages/");
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["multipart/form-data"] = new()
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = "object",
                            Required = new HashSet<string> { "file" },
                            Properties = new Dictionary<string, OpenApiSchema>
                            {
                                ["file"] = new OpenApiSchema
                                {
                                    Type = "string",
                                    Format = "binary",
                                    Description = multiple
                                        ? "Файл(ы) вложения; поле можно повторить (до 10 МБ каждый)"
                                        : "Изображение PNG/JPEG/WebP (до 5 МБ)"
                                }
                            }
                        }
                    }
                }
            };
        }

        // бинарные ответы для скачивания
        if (method == "GET" && (path == "/users/{user_id}/avatar" || path.StartsWith("/attachments/")))
        {
            operation.Responses["200"] = new OpenApiResponse
            {
                Description = "Бинарное содержимое файла",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/octet-stream"] = new()
                    {
                        Schema = new OpenApiSchema { Type = "string", Format = "binary" }
                    }
                }
            };
        }

        operation.Responses ??= [];
        foreach (var (prefix, m, codes) in ErrorMap)
        {
            if (!path.StartsWith(prefix) || (m is not null && m != method)) continue;
            foreach (var code in codes)
                if (!operation.Responses.ContainsKey(code))
                    operation.Responses[code] = new OpenApiResponse
                    {
                        Description = Describe(code, path),
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["application/json"] = new()
                            {
                                Schema = new OpenApiSchema
                                {
                                    Type = "object",
                                    Required = new HashSet<string> { "code", "error", "trace_id" },
                                    Properties = new Dictionary<string, OpenApiSchema>
                                    {
                                        ["code"] = new() { Type = "string", Description = "Машинный код ошибки" },
                                        ["error"] = new() { Type = "string", Description = "Человекочитаемое описание" },
                                        ["details"] = new() { Type = "object", Nullable = true },
                                        ["trace_id"] = new() { Type = "string", Description = "Идентификатор ошибки для логов" }
                                    }
                                }
                            }
                        }
                    };
            break;
        }
    }

    private static string Describe(string code, string path) => code switch
    {
        "400" => "Некорректные поля/query или пустой текст",
        "401" => path.StartsWith("/auth/login") ? "Неверный login или password"
            : "Нет, просрочена или удалена сессия (user_session_token)",
        "403" => "Сессия действительна, но прав на операцию нет (не участник, не владелец, не автор)",
        "404" => "Пользователь/чат/сообщение/вложение не найдено",
        "409" => path.StartsWith("/auth/register") ? "Указанный login уже занят"
            : path.EndsWith("/leave") ? "owner_transfer_required: сначала передайте владение"
            : "Конфликт состояния (message_deleted / content_not_stored)",
        "413" => "Файл превышает допустимый размер",
        "429" => "Превышен лимит: 30 запросов/мин на IP для POST /auth/* (заголовок Retry-After)",
        _ => "Ошибка"
    };
}

/// <summary>Required/длины/паттерны/enums для DTO и форматы дат в ответах.</summary>
public class DtoSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        var req = (schema.Required ??= new HashSet<string>());
        void Prop(string name, Action<OpenApiSchema> tune, bool required = true)
        {
            if (required) req.Add(name);
            if (schema.Properties.TryGetValue(name, out var s)) tune(s);
        }

        switch (context.SchemaRepository.Schemas.TryGetValue(context.Type.Name, out _)
                ? context.Type.Name : context.Type.Name)
        {
            case "RegisterIn":
            case "LoginIn":
                Prop("login", s => { s.MinLength = 3; s.MaxLength = 16; s.Pattern = "^[A-Za-z0-9_.\\-]{3,16}$"; });
                Prop("password", s => s.MinLength = 8);
                break;
            case "CreateChatIn":
                Prop("another_user_id", s => s.Minimum = 1);
                break;
            case "CreateGroupIn":
                Prop("title", s => { s.MinLength = 1; s.MaxLength = 64; });
                Prop("member_ids", s => s.UniqueItems = true);
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
                Prop("text", s => { s.MinLength = 1; s.MaxLength = 4096; });
                Prop("reply_to_id", s => s.Minimum = 1, required: false);
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
            case "SettingsIn":
                Prop("save_history", s => s.Nullable = false);
                break;
            case "MessageOut":
                Prop("content_state", s => s.Enum = [new OpenApiString("stored"), new OpenApiString("not_stored"), new OpenApiString("deleted")], required: true);
                Prop("text", s => s.Nullable = true, required: true);
                break;
            case "MemberOut":
                Prop("role", s => s.Enum = [new OpenApiString("member"), new OpenApiString("admin"), new OpenApiString("owner")]);
                break;
            case "DirectChatOut":
                Prop("type", s => s.Enum = [new OpenApiString("direct")]);
                break;
            case "GroupChatOut":
                Prop("type", s => s.Enum = [new OpenApiString("group")]);
                break;
            case "ErrorResponse":
                Prop("code", s => { });
                Prop("error", s => { });
                Prop("details", s => { }, required: false);
                Prop("trace_id", s => { });
                break;
        }

        // date-time для таймстампов в ответах
        if (schema.Properties.Count > 0)
            foreach (var (name, prop) in schema.Properties)
                if ((name.EndsWith("_at") || name.EndsWith("_date")) && prop.Type == "string")
                    prop.Format = "date-time";
    }
}
