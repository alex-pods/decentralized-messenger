# Реестр элементов интерфейса

Автоматическая инвентаризация XAML; наличие в списке не означает ручную проверку.

| Файл | Элемент | Назначение / привязка |
|---|---|---|
| Views/Dialogs/ForwardPicker.axaml | TextBox | Найти чат |
| Views/Dialogs/ForwardPicker.axaml | ListBox | {Binding Chats} |
| Views/Dialogs/ForwardPicker.axaml | ListBox |  |
| Views/Dialogs/ForwardPicker.axaml | Button | {Binding DataContext.ForwardToCommand, ElementName=Root} |
| Views/Dialogs/GroupDetails.axaml | TextBox | {Binding GroupTitle} / Название группы / Название группы |
| Views/Dialogs/GroupDetails.axaml | Button | Убрать из выбранных / {Binding DataContext.RemoveDraftCommand, ElementName=Root} |
| Views/Dialogs/MemberList.axaml | ListBox | {Binding Members} |
| Views/Dialogs/MemberList.axaml | ListBox |  |
| Views/Dialogs/MemberList.axaml | Button | OnMemberMenu / Управление участником |
| Views/Dialogs/PeopleSearch.axaml | TextBox | {Binding SearchQuery} / Например, @alex |
| Views/Dialogs/PeopleSearch.axaml | TextBox |  |
| Views/Dialogs/PeopleSearch.axaml | TextBox |  |
| Views/Dialogs/PeopleSearch.axaml | Button | Найти / {Binding SearchCommand} |
| Views/Dialogs/PeopleSearch.axaml | ListBox | {Binding SearchResults} |
| Views/Dialogs/PeopleSearch.axaml | ListBox |  |
| Views/Dialogs/PeopleSearch.axaml | Button | {Binding DataContext.PickUserCommand, ElementName=Root} |
| Views/Dialogs/ProfileEditor.axaml | Button | Выбрать фото… / OnChooseAvatarClick |
| Views/Dialogs/ProfileEditor.axaml | TextBox | {Binding DisplayName} / Имя |
| Views/Dialogs/ProfileEditor.axaml | TextBox | {Binding Bio} / О себе |
| Views/Dialogs/ProfileEditor.axaml | CheckBox | Находить меня по тегу |
| Views/Dialogs/ProfileEditor.axaml | Button | Сохранить / {Binding SaveProfileCommand} |
| Views/LoginView.axaml | TextBox | {Binding Login} / Введите логин |
| Views/LoginView.axaml | TextBox | {Binding Password} / Введите пароль |
| Views/LoginView.axaml | TextBox |  |
| Views/LoginView.axaml | Button | Показать пароль / OnTogglePassword |
| Views/LoginView.axaml | TextBox |  |
| Views/LoginView.axaml | Button | {Binding AuthAction} / {Binding SubmitCommand} |
| Views/LoginView.axaml | Button | {Binding SwitchModeCommand} |
| Views/LoginView.axaml | Button | {Binding ToggleConnectionCommand} |
| Views/LoginView.axaml | TextBox | {Binding Server} / https://…/messenger |
| Views/ShellView.axaml | TextBox | Поиск чатов / Поиск чатов |
| Views/ShellView.axaml | TextBox |  |
| Views/ShellView.axaml | Button | OnNewConversation / Начать разговор |
| Views/ShellView.axaml | Button | OnAccountMenu |
| Views/ShellView.axaml | ListBox | {Binding Chats} |
| Views/ShellView.axaml | ListBox |  |
| Views/ShellView.axaml | Button | OnShowChats |
| Views/ShellView.axaml | Button | Информация о чате / {Binding ShowMembersCommand} |
| Views/ShellView.axaml | Button | {Binding ShowPinnedCommand} |
| Views/ShellView.axaml | Button | Открепить / {Binding UnpinShownCommand} |
| Views/ShellView.axaml | Button | {Binding ShowDirectCommand} |
| Views/ShellView.axaml | Button | {Binding ShowGroupCommand} |
| Views/ShellView.axaml | ListBox | {Binding Messages} |
| Views/ShellView.axaml | ListBox |  |
| Views/ShellView.axaml | ListBox |  |
| Views/ShellView.axaml | Button | OnMessageMenuClick / Действия с сообщением |
| Views/ShellView.axaml | Button | {Binding LoadOlderCommand} |
| Views/ShellView.axaml | Button | Отменить / {Binding CancelComposeCommand} |
| Views/ShellView.axaml | TextBox | {Binding Draft, UpdateSourceTrigger=PropertyChanged} / Напишите сообщение… |
| Views/ShellView.axaml | TextBox |  |
| Views/ShellView.axaml | Button | Отправить / {Binding SendCommand} |
| Views/ShellView.axaml | Button | Закрыть / {Binding CloseOverlayCommand} |
| Views/ShellView.axaml | Button | Создать группу / {Binding CreateGroupCommand} |
| Views/ShellView.axaml | Button | Скрыть чат у себя / {Binding HideChatCommand} |
| Views/ShellView.axaml | Button | Выйти из группы / OnLeaveGroup |
| Views/ShellView.axaml | Button | Закрыть ошибку / {Binding DismissErrorCommand} |

Динамические меню: новый чат/группа; профиль/оформление/выход; реакции, ответ, пересылка, закреп, редактирование, удаление; роли/исключение/передача владения.
Оформление: пресеты, имя, переключатель тёмных элементов, четыре color picker, отмена, применение.

Обмен темами: импорт/экспорт файла, поле HTTPS-ссылки, загрузка, публикация копии, копирование ссылки, обновление своей публикации, состояние операции/ошибки.
