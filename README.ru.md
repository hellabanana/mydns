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
- `EXCLUDE_REDIRECT` - домены, которые нужно исключить из rewrites.

Несколько источников или доменов указываются через запятую:

```text
BLOCK=https://first.example/hosts,https://second.example/hosts
REDIRECT=https://example.com/redirect-hosts
EXCLUDE_REDIRECT=instagram.com,twitch.com
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

Если `BLOCK` пуст, Denylist не изменяется. Если `REDIRECT` пуст, Rewrites не изменяются. Если одновременно пусты `BLOCK` и `REDIRECT`, приложение удаляет все Denylist и Rewrites в профиле.

## GitHub Actions

1. Сделайте fork репозитория.
2. Перейдите в Settings -> Environments.
3. Создайте environment с именем `DNS`.
4. Добавьте `CLIENT_ID` и `AUTH_SECRET` в Environment secrets.
5. Добавьте `BLOCK`, `REDIRECT` и `EXCLUDE_REDIRECT` в Environment variables.

Workflow запускается ежедневно в `01:30 UTC` и может быть запущен вручную через вкладку Actions.

Чтобы изменить расписание, отредактируйте cron в `.github/workflows/github_action.yml`.

## Локальный запуск

Требуется .NET 10 SDK.

```shell
dotnet restore
dotnet build
dotnet run
```

Для проверки парсинга без обращения к NextDNS API:

```shell
dotnet run -- --self-test
```
