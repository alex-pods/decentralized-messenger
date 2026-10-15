using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MessengerBackend;

/// <summary>Конфигурация Swagger: единый Bearer для REST + фильтры контракта.</summary>
public static class SwaggerSetup
{
    public static void Configure(SwaggerGenOptions o)
    {
        o.SwaggerDoc("v1", new()
        {
            Title = "Messenger Backend API",
            Version = "4.0.0 (MVP: обычные чаты через сервер)",
            Description = "Обычные личные и групповые чаты с сохранением истории на сервере. " +
                          "Регистрация/вход/выход, профили и аватары, поиск, участники и роли (member/admin/owner), " +
                          "сообщения и ответы, идемпотентная отправка (client_ref), редактирование, удаление " +
                          "у себя и у всех, реакции, вложения, история с курсорной пагинацией, прочтения, " +
                          "WebSocket follow(chat_id).\n\n" +
                          "REST-авторизация — только Authorization: Bearer <user_session_token>. " +
                          "WebSocket — query-параметр user_session_token. Протокол WS: GET /ws/protocol.\n\n" +
                          "Архитектурно заложено расширение режимом секретных чатов (без хранения содержимого) " +
                          "и собственными узлами; в этой версии они не реализованы и не объявлены."
        });
        o.OperationFilter<AuthAndErrorsOperationFilter>();
        o.SchemaFilter<DtoSchemaFilter>();
        o.AddSecurityDefinition("SessionBearer", new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "user_session_token",
            Description = "Authorization: Bearer <user_session_token> — единственный способ REST-авторизации"
        });
    }
}

/// <summary>Security на защищённых ручках; ErrorResponse-схема на кодах ошибок;
/// multipart-тела файловых POST; бинарные ответы скачивания.</summary>
public class AuthAndErrorsOperationFilter : IOperationFilter
{
    private static readonly string[] PublicPrefixes = ["/auth/register", "/auth/login", "/health", "/ws/protocol"];

    private static readonly (string Prefix, string? Method, string[] Codes)[] ErrorMap =
    [
        ("/auth/register", "POST", ["400", "409", "429"]),
        ("/auth/login", "POST", ["400", "401", "429"]),
        ("/auth/logout", "POST", ["401"]),
        ("/users/me/avatar", "POST", ["400", "401", "413"]),
        ("/users/me/avatar", "GET", ["401", "404"]),
        ("/users/me", "PATCH", ["400", "401"]),
        ("/users/me", "GET", ["401"]),
        ("/users/search", "GET", ["400", "401"]),
        ("/users/", "GET", ["401", "404"]),
        ("/chats/group", "POST", ["400", "401", "404"]),
        ("/chats/{chat_id}/members/{user_id}", "DELETE", ["400", "401", "403", "404"]),
        ("/chats/{chat_id}/members/{user_id}", "PATCH", ["400", "401", "403", "404"]),
        ("/chats/{chat_id}/owner", "POST", ["400", "401", "403", "404"]),
        ("/chats/{chat_id}/members", "POST", ["400", "401", "403", "404"]),
        ("/chats/{chat_id}/members", "GET", ["401", "403", "404"]),
        ("/chats/{chat_id}/messages", "GET", ["400", "401", "403", "404"]),
        ("/chats/{chat_id}/messages", "POST", ["400", "401", "403", "404", "409"]),
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
        // Swashbuckle 6.5 не переносит WithSummary/WithDescription — делаем вручную
        var meta = context.ApiDescription.ActionDescriptor.EndpointMetadata.ToList();
        var summary = meta.OfType<Microsoft.AspNetCore.Http.Metadata.IEndpointSummaryMetadata>().LastOrDefault()?.Summary;
        var description = meta.OfType<Microsoft.AspNetCore.Http.Metadata.IEndpointDescriptionMetadata>().LastOrDefault()?.Description;
        if (!string.IsNullOrWhiteSpace(summary)) operation.Summary = summary;
        if (!string.IsNullOrWhiteSpace(description)) operation.Description = description;

        var path = System.Text.RegularExpressions.Regex.Replace(
            "/" + (context.ApiDescription.RelativePath ?? "").TrimStart('/'),
            @"\{(\w+):[^}]+\}", "{$1}");
        var method = context.ApiDescription.HttpMethod?.ToUpperInvariant();
        var isPublic = PublicPrefixes.Any(path.StartsWith);

        operation.Security = isPublic ? [] : [new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "SessionBearer" }
            }] = []
        }];

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
                                ["file"] = multiple
                                    ? new OpenApiSchema
                                    {
                                        Type = "array",
                                        Description = "Файлы вложения, каждый до 10 МБ; несколько — повторением поля",
                                        Items = new OpenApiSchema { Type = "string", Format = "binary" }
                                    }
                                    : new OpenApiSchema
                                    {
                                        Type = "string",
                                        Format = "binary",
                                        Description = "Изображение PNG/JPEG/WebP (до 5 МБ)"
                                    }
                            }
                        }
                    }
                }
            };
        }

        if (method == "GET" && path == "/chats/{chat_id}/messages")
        {
            operation.Parameters ??= [];
            foreach (var p in new (string Name, string Desc, string Def)[]
                     {
                         ("limit", "Размер страницы: целое 1..100 (по умолчанию 50)", "50"),
                         ("before_id", "Пагинация назад: только сообщения с id СТРОГО МЕНЬШЕ курсора; без него — от самых последних", null),
                         ("after_id", "Синхронизация после переподключения: только сообщения с id СТРОГО БОЛЬШЕ курсора, по возрастанию; листать до пустой страницы", null),
                     })
                if (!operation.Parameters.Any(x => x.Name == p.Name))
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = p.Name, In = ParameterLocation.Query, Required = false,
                        Description = p.Desc,
                        Schema = new OpenApiSchema { Type = p.Name == "limit" ? "integer" : "integer", Format = "int64", Minimum = 1, Maximum = p.Name == "limit" ? 100 : null, Default = p.Def is null ? null : new OpenApiLong(long.Parse(p.Def)) }
                    });
        }

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
                    },
                    ["text/plain"] = new() { Schema = new OpenApiSchema { Type = "string", Format = "binary" } },
                    ["image/png"] = new() { Schema = new OpenApiSchema { Type = "string", Format = "binary" } },
                    ["image/jpeg"] = new() { Schema = new OpenApiSchema { Type = "string", Format = "binary" } },
                    ["image/webp"] = new() { Schema = new OpenApiSchema { Type = "string", Format = "binary" } }
                }
            };
        }

        operation.Responses ??= [];
        foreach (var (prefix, m, codes) in ErrorMap)
        {
            if (!path.StartsWith(prefix) || (m is not null && m != method)) continue;
            // 415 реально возможен у любой операции с JSON-телом
            if (method is "POST" or "PATCH" && operation.RequestBody?.Content.ContainsKey("application/json") == true
                && !operation.Responses.ContainsKey("415"))
                operation.Responses["415"] = new OpenApiResponse
                {
                    Description = "Content-Type не поддерживается",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new() { Schema = ErrorSchema() }
                    }
                };
            ApplyResponseSchemas(operation, context, path, method);
            foreach (var code in codes)
                if (!operation.Responses.ContainsKey(code))
                    operation.Responses[code] = new OpenApiResponse
                    {
                        Description = Describe(code, path),
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["application/json"] = new()
                            {
                                Schema = ErrorSchema()
                            }
                        }
                    };
            break;
        }
    }

    static void ApplyResponseSchemas(OpenApiOperation operation, OperationFilterContext ctx, string path, string method)
    {
        OpenApiSchema Ref(string id) => new()
        { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = id } };

        OpenApiSchema ChatOneOf() => new()
        {
            OneOf = [Ref("DirectChatOut"), Ref("GroupChatOut")]
        };
        void Set200(string desc, OpenApiSchema schema)
        {
            operation.Responses["200"] = new OpenApiResponse
            {
                Description = desc,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new() { Schema = schema }
                }
            };
        }

        if (method == "GET" && path == "/chats")
            Set200("Список чатов (direct с other_user / group с title), + last_message",
                new OpenApiSchema { Type = "array", Items = ChatOneOf() });
        else if (method == "GET" && path == "/chats/{chat_id}")
            Set200("Карточка чата: direct или group", ChatOneOf());
        else if (method == "POST" && path == "/messages/{message_id}/attachments")
            operation.Responses["201"] = new OpenApiResponse
            {
                Description = "Массив созданных вложений",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new()
                    {
                        Schema = new OpenApiSchema { Type = "array", Items = Ref("AttachmentOut") }
                    }
                }
            };
        else if (method == "POST" && path == "/users/me/avatar")
            operation.Responses["201"] = new OpenApiResponse
            {
                Description = "Путь сохранённого файла",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new()
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = "object",
                            Required = new HashSet<string> { "path_to_avatar_file" },
                            Properties = new Dictionary<string, OpenApiSchema>
                            {
                                ["path_to_avatar_file"] = new() { Type = "string" }
                            }
                        }
                    }
                }
            };
        else if (method == "GET" && path == "/health")
            Set200("Состояние сервиса", new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["status"] = new() { Type = "string" },
                    ["service"] = new() { Type = "string" },
                    ["version"] = new() { Type = "string" }
                }
            });
        else if (method == "POST" && path == "/auth/register")
            operation.Responses["201"] = new OpenApiResponse
            { Description = "Пользователь создан; тело ответа пустое" };
    }

    static OpenApiSchema ErrorSchema() => new()
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
    };

    static string Describe(string code, string path) => code switch
    {
        "400" => "Некорректные поля/query или пустой текст",
        "401" => path.StartsWith("/auth/login") ? "Неверный login или password"
            : "Нет, просрочена или удалена сессия (Bearer)",
        "403" => "Сессия действительна, но прав на операцию нет (не участник, не владелец, не автор)",
        "404" => "Пользователь/чат/сообщение/вложение не найдено",
        "409" => path.StartsWith("/auth/register") ? "Указанный login уже занят"
            : path.EndsWith("/leave") ? "owner_transfer_required: сначала передайте владение"
            : path.EndsWith("/messages") ? "client_ref_conflict: ключ отправки уже использован с другим содержимым"
            : "Конфликт состояния (message_deleted / content_not_stored)",
        "413" => "Файл превышает допустимый размер",
        "429" => "Превышен лимит 30 запросов/мин на IP (заголовок Retry-After)",
        _ => "Ошибка"
    };
}

/// <summary>Required/длины/паттерны/enums для DTO; date-time для таймстампов ответов.</summary>
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

        switch (context.Type.Name)
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
