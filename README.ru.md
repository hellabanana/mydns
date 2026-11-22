Languages: [English](README.md) | [Русский](README.ru.md)

# DNS Block&Redirect Configurer для NextDNS

Консольное .NET-приложение для автоматической настройки блокировок и DNS-перенаправлений в профилях NextDNS.

Проект рассчитан на запуск через GitHub Actions: достаточно сделать fork, указать секреты и переменные окружения, после чего правила будут обновляться ежедневно или вручную через Run workflow.

## Возможности

- Загружает один или несколько hosts-файлов из URL.
- Добавляет домены из `BLOCK` в NextDNS Denylist.
- Добавляет пары `IP -> домен` из `REDIRECT` в NextDNS Rewrites.
- Обновляет rewrite, если IP для существующего домена изменился.
- Удаляет rewrite для доменов из `EXCLUDE_REDIRECT` и не добавляет их снова.
- Поддерживает несколько профилей NextDNS через значения `CLIENT_ID` и `AUTH_SECRET`, разделенные запятыми.
- Учитывает лимиты NextDNS API и повторяет запросы после ответа `429` или `524`.

## Переменные окружения

### Обязательные

- `CLIENT_ID` - ID профиля NextDNS. Его можно найти в профиле NextDNS в секции Endpoints.
- `AUTH_SECRET` - API key из https://my.nextdns.io/account.

Для нескольких профилей укажите значения через запятую без пробелов:

```text
CLIENT_ID=profile_1,profile_2
AUTH_SECRET=api_key_1,api_key_2
```

Количество значений в `CLIENT_ID` и `AUTH_SECRET` должно совпадать.

### Опциональные

- `BLOCK` - ссылки на hosts-файлы для блокировки доменов.
- `REDIRECT` - ссылки на hosts-файлы для DNS-перенаправлений.
- `REQUIRED_REDIRECT` — домены или шаблоны через запятую для приоритетного добавления из `REDIRECT`. Шаблон `*.openai.com` включает `openai.com` и все его поддомены, а `*.openai.*` также включает имена с другими окончаниями, например `openai.net`. Одиночная `*` выбирает все домены. Исключения из `EXCLUDE_REDIRECT` действуют и для шаблонов. Если у шаблона нет подходящих записей или обязательные правила не помещаются в профиль, синхронизация сообщит об ошибке.
- `EXCLUDE_REDIRECT` — исключения через запятую. Значение без точки, например `rutor`, совпадает с любой частью имени домена. Значение с точкой проверяется по окончанию имени. Регистр не учитывается.

Несколько источников или доменов указываются через запятую:

```text
BLOCK=https://first.example/hosts,https://second.example/hosts
REDIRECT=https://example.com/redirect-hosts
REQUIRED_REDIRECT=*.openai.*,*.chatgpt.com
EXCLUDE_REDIRECT=rutor,instagram.com,twitch.com
```

## Формат hosts-файлов

Для `BLOCK` используются строки с блокирующими IP:

```text
0.0.0.0 domain.to.block
127.0.0.1 another.domain.to.block
::1 ipv6.domain.to.block
```

Для `REDIRECT` используются строки с реальными IP:

```text
1.2.3.4 domain.to.redirect
5.6.7.8 another.domain.to.redirect
```

Строки-комментарии и inline-комментарии после `#` игнорируются. Префикс `www.` удаляется.

## Поведение

Если указан `BLOCK`, приложение скачивает источники, оставляет только домены со строками `0.0.0.0`, `127.0.0.1` и `::1`, затем добавляет отсутствующие домены в NextDNS Denylist.

Если указан `REDIRECT`, приложение скачивает источники, оставляет пары `IP домен`, где IP не является блокирующим, сравнивает их с существующими NextDNS Rewrites и:

- не трогает совпадающие правила;
- удаляет устаревшие правила, если IP изменился;
- добавляет новые правила;
- удаляет и пропускает домены из `EXCLUDE_REDIRECT`.

В начале синхронизации каждого профиля приложение удаляет существующие rewrites, которые совпадают с `EXCLUDE_REDIRECT`, даже если `REDIRECT` пуст. Если задан только `EXCLUDE_REDIRECT`, выполняется только эта очистка. Если одновременно пусты `BLOCK`, `REDIRECT` и `EXCLUDE_REDIRECT`, приложение удаляет все записи Denylist и Rewrites в профиле.

## GitHub Actions

1. Сделайте fork репозитория.
2. Перейдите в Settings -> Environments.
3. Создайте environment с именем `DNS`.
4. Добавьте `CLIENT_ID` и `AUTH_SECRET` в Environment secrets.
5. Добавьте нужные переменные `BLOCK`, `REDIRECT`, `REQUIRED_REDIRECT` и `EXCLUDE_REDIRECT` в Environment variables.

Workflow запускается ежедневно в `01:30 UTC` и может быть запущен вручную через вкладку Actions.

Чтобы изменить расписание, отредактируйте cron в `.github/workflows/github_action.yml`.

## Локальный запуск

Требуется .NET 10 SDK.

```shell
dotnet restore
dotnet build
```

Для запуска с тестовым профилем NextDNS скопируйте `.env.example` в `.env` и укажите ID профиля и ключ API. Запустите приложение из каталога проекта. Оно загрузит `.env` из рабочей папки. Уже заданные переменные окружения имеют приоритет.

```shell
dotnet run
```

Для запуска самопроверки без обращения к NextDNS API:

```shell
dotnet run -- --self-test
```
