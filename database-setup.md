# Налаштування бази даних

У репозиторії навмисно відсутні реальні облікові дані. Скористайтеся інструкцією нижче, щоб підготувати локальний MySQL без розкриття секретів у публічному коді.

Потрібен MySQL 8.0.13 або новіший: міграції використовують функціональні унікальні індекси для нормалізованих областей конфігурації.

## Підготовка бази

- Створіть локального користувача та схему (наприклад, `schedule_user` і `schedule_db`).  
  ```sql
  CREATE DATABASE schedule_db CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
  CREATE USER 'schedule_user'@'localhost' IDENTIFIED BY 'ВАШ_ПАРОЛЬ';
  GRANT ALL PRIVILEGES ON schedule_db.* TO 'schedule_user'@'localhost';
  FLUSH PRIVILEGES;
  ```
- Переконайтеся, що MySQL запущено й порт 3306 доступний.

## Конфігурація секретів для розробки

Виконуйте команди з кореня репозиторію. Ідентифікатор Secret Manager уже налаштований у серверному проєкті.

1. Додайте рядок підключення до сховища:
   ```
   dotnet user-secrets set "ConnectionStrings:Default" "Server=localhost;Port=3306;Database=schedule_db;User=schedule_user;Password=ВАШ_ПАРОЛЬ;CharSet=utf8mb4;TreatTinyAsBoolean=true;AllowPublicKeyRetrieval=True;SslMode=None" --project BlazorWasmDotNet8AspNetCoreHosted.Server/BlazorWasmDotNet8AspNetCoreHosted.Server.csproj
   ```

Під час запуску застосунок читає конфігурацію з таких джерел (від нижчого до вищого пріоритету):

1. `appsettings.json` і `appsettings.{Environment}.json` (несекретні значення);
2. Secret Manager (`dotnet user-secrets`);
3. змінні середовища.

Переконайтеся, що знайдено значення `ConnectionStrings:Default`, інакше застосунок та інструменти EF не зможуть виконати запити до MySQL.

## Застосування міграцій

- Якщо команда `dotnet ef` недоступна, відновіть локальні інструменти: `dotnet tool restore`.
- Створення нової міграції:
  ```
  dotnet ef migrations add <НазваМіграції> --project BlazorWasmDotNet8AspNetCoreHosted.Server
  ```
- Накочування схеми на локальну базу:
  ```
  dotnet tool restore
  dotnet ef database update --project BlazorWasmDotNet8AspNetCoreHosted.Server
  ```
- За потреби видалення останньої не застосованої міграції:
  ```
  dotnet ef migrations remove --project BlazorWasmDotNet8AspNetCoreHosted.Server
  ```

Важливо: коли оновлюєте версію застосунку на сервері, проганяйте міграції через `dotnet ef database update --project BlazorWasmDotNet8AspNetCoreHosted.Server`.

Актуальне оновлення схеми також додає `ScheduleItems.BatchKey` для атомарної роботи з одним логічним заняттям і унікальні індекси конфігурації для кодів типів/модулів, слотів часу, календарних винятків, обіду та ліміту першого слота. Міграція нормалізує безпечні значення, але зупиниться на неоднозначних дублях замість мовчазної втрати даних. Перед оновленням робочої бази зробіть резервну копію та перевірте процедуру у staging-середовищі.

> **Примітка:** міграції створюють лише структуру БД. Під час старту сервера автоматично додаються тільки базові типи занять (BREAK, CANCELED, RESCHEDULED, EXAM, CREDIT, NONE); решту довідників потрібно наповнювати вручну або власним сидингом чи SQL-скриптом, який можна запускати після `dotnet ef database update`.

## Операційні таблиці автогенерації

Поточна схема містить таблицю `AutoGenJobRuns`. Вона потрібна для збереження стану довгих запусків автогенерації: `JobId`, тип запуску, статус, прогрес, діапазон дат, кількість створених/пропущених занять, JSON параметрів, результат, звіт та помилка.

Після оновлення застосунку обов'язково застосуйте міграції:

```
dotnet tool restore
dotnet ef database update --project BlazorWasmDotNet8AspNetCoreHosted.Server
```

Відсутність `AutoGenJobRuns` або будь-якої іншої актуальної міграції є непідтримуваним станом розгортання: `GET /health/ready` поверне `503`, а екземпляр не слід підключати до трафіку. Спочатку виконайте `dotnet ef database update`, переконайтеся, що readiness повертає `200`, і лише після цього запускайте автогенерацію.
