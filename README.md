# BlazorWasmDotNet8AspNetCoreHosted

## Огляд

Це рішення демонструє повноцінну систему планування модулів: клієнт на Blazor WebAssembly (`Client`), серверне ASP.NET Core API (`Server`) та спільну бібліотеку DTO (`Shared`). Цільова платформа — .NET 8. Для зберігання даних використовується MySQL 8.0.13 або новіший із провайдером Pomelo.EntityFrameworkCore.MySql. У середовищі розробки доступний Swagger для дослідження API.

## Структура рішення

- `BlazorWasmDotNet8AspNetCoreHosted.Client` — вебклієнт Blazor WASM, Razor-компоненти та клієнтські сервіси.
- `BlazorWasmDotNet8AspNetCoreHosted.Server` — ASP.NET Core хост, контролери, DI-налаштування, контекст EF Core та міграції.
- `BlazorWasmDotNet8AspNetCoreHosted.Shared` — спільні DTO й контракти, що використовуються клієнтом і сервером.
- `database-setup.md` для детального налаштування бази даних.

## Безпека виробничого розгортання

Серверний API поки не має автентифікації та рольової авторизації. До інтеграції з університетським SSO/JWT не публікуйте застосунок у відкриту мережу: обмежте доступ приватною мережею, VPN або захищеним reverse proxy. Перед повноцінним запуском потрібно окремо визначити ролі щонайменше для перегляду, укладання розкладу, публікації та адміністрування довідників.

Для оркестратора доступні перевірки `GET /health/live` та `GET /health/ready`. Readiness повертає `503`, якщо база недоступна, початковий сидинг не завершився, залишилися незастосовані міграції або відсутні обов'язкові індекси цілісності.

Якщо TLS завершується на reverse proxy, передайте його точну IP-адресу через `ReverseProxy__KnownProxies__0` (наступні адреси — з індексами `__1`, `__2` тощо). Сервер приймає `X-Forwarded-For`/`X-Forwarded-Proto` лише від цих довірених адрес; не вмикайте глобальну довіру до forwarded-заголовків.

## Попередні вимоги

- Встановлений .NET SDK 8.0.100 або новіший (`dotnet --list-sdks`).
- Локальні .NET-інструменти репозиторію: після клонування виконайте `dotnet tool restore`.
- Доступ до локального екземпляра MySQL 8.0.13 або новішого (або сумісного керованого сервісу). Цей мінімум потрібен для функціональних унікальних індексів конфігурації.
- Node.js/npm знадобиться лише за умови зміни фронтенд-інструментів; для запуску поточного рішення не є обов’язковим.

## Швидкий старт

1. Клонуйте репозиторій.
2. Перевірте, що рішення коректно збирається:
   ```
   dotnet build BlazorWasmDotNet8AspNetCoreHosted.Server.sln
   ```
3. Налаштуйте рядок підключення до бази даних (розділ нижче).
4. Застосуйте міграції:
   ```
   dotnet tool restore
   dotnet ef database update --project BlazorWasmDotNet8AspNetCoreHosted.Server
   ```
5. Запустіть серверну частину:
   ```
   dotnet run --project BlazorWasmDotNet8AspNetCoreHosted.Server
   ```

## Налаштування бази даних

Реальні облікові дані навмисно не зберігаються в репозиторії. Перед запуском підставте валідний рядок підключення, скориставшись одним із способів:

- **Secret Manager (рекомендований локально):**
  ```
  dotnet user-secrets set "ConnectionStrings:Default" "Server=localhost;Port=3306;Database=schedule_db;User=schedule_user;Password=YOUR_PASSWORD;CharSet=utf8mb4;TreatTinyAsBoolean=true;AllowPublicKeyRetrieval=True;SslMode=None" --project BlazorWasmDotNet8AspNetCoreHosted.Server/BlazorWasmDotNet8AspNetCoreHosted.Server.csproj
  ```
- **Змінна середовища:**
  ```powershell
  $env:ConnectionStrings__Default = "Server=localhost;Port=3306;Database=schedule_db;User=schedule_user;Password=YOUR_PASSWORD;CharSet=utf8mb4;TreatTinyAsBoolean=true;AllowPublicKeyRetrieval=True;SslMode=None"
  ```

Детальні покрокові інструкції зосереджені у `database-setup.md`.

> **Застереження:** наявні міграції створюють лише структуру таблиць. Під час запуску сервера автоматично додаються лише базові типи занять (BREAK, CANCELED, RESCHEDULED, EXAM, CREDIT, NONE). Інші довідники або демонстраційні значення потрібно наповнювати окремо.

## Робота з Entity Framework Core

- Якщо команда `dotnet ef` недоступна, відновіть локальні інструменти: `dotnet tool restore`.
- Додати міграцію:
  ```
  dotnet ef migrations add <MigrationName> --project BlazorWasmDotNet8AspNetCoreHosted.Server
  ```
- Оновити базу даних:
  ```
  dotnet ef database update --project BlazorWasmDotNet8AspNetCoreHosted.Server
  ```
- Скасувати останню міграцію (якщо вона ще не застосована до бази):
  ```
  dotnet ef migrations remove --project BlazorWasmDotNet8AspNetCoreHosted.Server
  ```

Design-time фабрика `DesignTimeDbContextFactory` зчитує `ConnectionStrings:Default` із шарів конфігурації (`appsettings*.json`, Secret Manager, змінні середовища). Перед запуском інструментів EF переконайтеся, що рядок доступний.

## Запуск окремих проєктів

- **Сервер:** `dotnet run --project BlazorWasmDotNet8AspNetCoreHosted.Server`
- **Клієнт:** хоститься сервером, тому додатковий запуск не потрібен.
- **Публікація:** `dotnet publish BlazorWasmDotNet8AspNetCoreHosted.Server -c Release`

## Усунення несправностей

- **`Unable to connect to any of the specified MySQL hosts`** — перевірте, чи запущено MySQL та чи правильні хост і порт у рядку підключення.
- **`Рядок підключення 'ConnectionStrings:Default' не налаштовано`** — додайте рядок через Secret Manager, змінні середовища або локальний конфіг перед запуском EF чи сервера.
- **CORS або 404 для API** — впевніться, що сервер активний, а клієнт звертається до актуального базового URL.

## Додаткові матеріали

- [operations-guide.md](operations-guide.md) - Покроковий посібник з операційного запуску проекту.
- [database-setup.md](database-setup.md) - Інструкція з налаштування бази даних та підключення.

## Поточні примітки щодо автогенерації

- Автогенерація розкладу працює через чернетки викладачів і підтримує preflight-перевірку, soft fill, repair-pass, перевірку переходів між корпусами, контроль аудиторій, викладачів і перетинів.
- Стан довгих запусків автогенерації зберігається в таблиці `AutoGenJobRuns`: параметри запуску, статус, прогрес, результат, звіт, помилка та часові мітки. Після оновлення потрібно застосувати міграції.
- Усі запуски виконуються через job API `POST /api/teacher-drafts/autogen/jobs`; застарілі синхронні маршрути повертають `410 Gone`, щоб їх не можна було використати в обхід лімітів і черги.
- Для довгих діапазонів використовуйте фоновий job API/UI, а не короткий синхронний запуск. Незавершене завдання не поновлюється після перезапуску: під час першого читання його збережений `Queued`/`Running` автоматично переходить у `Failed`, після чого запуск можна безпечно повторити. Перед плановим обслуговуванням дочекайтеся завершення активної черги.
- Блокування виконання job є локальним для процесу. До впровадження розподіленого lease/worker запускайте сервер лише в одному екземплярі; кілька replica можуть одночасно змінювати ті самі чернетки.
- Hard-rule validator винесений у серверний валідатор і запускається перед збереженням нових чернеток.

Приклад локального запуску сервера з HTTP-профілем:

```
dotnet run --project BlazorWasmDotNet8AspNetCoreHosted.Server --launch-profile http
```

Типова адреса з поточних launch settings: `http://localhost:5285`.

