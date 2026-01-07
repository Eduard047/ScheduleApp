# Налаштування бази даних

У репозиторії навмисно відсутні реальні облікові дані. Скористайтеся інструкцією нижче, щоб підготувати локальний MySQL без розкриття секретів у публічному коді.

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

1. Перейдіть до директорії сервера:
   ```
   cd BlazorWasmDotNet8AspNetCoreHosted.Server
   ```
2. Ініціалізуйте сховище секретів (достатньо одного разу на машину):
   ```
   dotnet user-secrets init
   ```
3. Додайте рядок підключення до сховища:
   ```
   dotnet user-secrets set "ConnectionStrings:Default" "Server=localhost;Port=3306;Database=schedule_db;User=schedule_user;Password=ВАШ_ПАРОЛЬ;CharSet=utf8mb4;TreatTinyAsBoolean=true;AllowPublicKeyRetrieval=True;SslMode=None"
   ```

Під час запуску застосунок читає конфігурацію з таких джерел (у порядку пріоритету):

1. `appsettings.json` і `appsettings.{Environment}.json` (несекретні значення);
2. Secret Manager (`dotnet user-secrets`);
3. змінні середовища.

Переконайтеся, що знайдено значення `ConnectionStrings:Default`, інакше застосунок та інструменти EF не зможуть виконати запити до MySQL.

## Застосування міграцій

- Якщо команда `dotnet ef` недоступна, встановіть `dotnet-ef`: `dotnet tool install --global dotnet-ef`.
- Створення нової міграції:
  ```
  dotnet ef migrations add <НазваМіграції> --project BlazorWasmDotNet8AspNetCoreHosted.Server
  ```
- Накочування схеми на локальну базу:
  ```
  dotnet ef database update --project BlazorWasmDotNet8AspNetCoreHosted.Server
  ```
- За потреби видалення останньої не застосованої міграції:
  ```
  dotnet ef migrations remove --project BlazorWasmDotNet8AspNetCoreHosted.Server
  ```

Важливо: коли оновлюєте версію застосунку на сервері, проганяйте міграції через `dotnet ef database update --project BlazorWasmDotNet8AspNetCoreHosted.Server`.

> **Примітка:** міграції створюють лише структуру БД. Під час старту сервера автоматично додаються тільки базові типи занять (BREAK, CANCELED, RESCHEDULED, EXAM, CREDIT); решту довідників потрібно наповнювати вручну або власним сидингом чи SQL-скриптом, який можна запускати після `dotnet ef database update`.
