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
            Version = "4.1.0",
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
