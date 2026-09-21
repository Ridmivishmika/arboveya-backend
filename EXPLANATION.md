# Complete Beginner's Guide: How This Backend Works

Welcome! If you are new to backend development, don't worry. This guide explains **every single concept, folder, and file** in plain English with easy-to-understand real-world analogies.

---

## 📑 Table of Contents
1. [The Big Picture: What is a Backend?](#1-the-big-picture-what-is-a-backend)
2. [What is a DTO (Data Transfer Object)?](#2-what-is-a-dto-data-transfer-object)
3. [What is a Model (Database Entity)?](#3-what-is-a-model-database-entity)
4. [Why Keep Models and DTOs Separate?](#4-why-keep-models-and-dtos-separate)
5. [What is Entity Framework Core (EF Core) & DbContext?](#5-what-is-entity-framework-core-ef-core--dbcontext)
6. [What are Migrations?](#6-what-are-migrations)
7. [How Does Password Hashing (BCrypt) Work?](#7-how-does-password-hashing-bcrypt-work)
8. [What is JWT (JSON Web Token)?](#8-what-is-jwt-json-web-token)
9. [What is a Controller?](#9-what-is-a-controller)
10. [What is a Service & Dependency Injection?](#10-what-is-a-service--dependency-injection)
11. [What is Program.cs?](#11-what-is-programcs)
12. [What is Swagger UI?](#12-what-is-swagger-ui)
13. [Step-by-Step Flow: What Happens When Someone Registers & Logs In?](#13-step-by-step-flow-what-happens-when-someone-registers--logs-in)
14. [Folder & File Directory Tour](#14-folder--file-directory-tour)

---

## 1. The Big Picture: What is a Backend?

Think of a **restaurant**:
- **The Customer** (Browser / Mobile App / Frontend): Looks at the menu and orders food.
- **The Waiter** (Controller / API): Takes the order from the table and carries it to the kitchen.
- **The Chef** (Service / Business Logic): Prepares the food according to specific rules.
- **The Pantry / Fridge** (Database - PostgreSQL): Where all raw ingredients (users, passwords, data) are stored safely.

The **Backend** is the kitchen, waiter, and pantry combined. It ensures that only valid orders are processed and data is stored safely.

---

## 2. What is a DTO (Data Transfer Object)?

### Analogy: An Envelope vs A Filing Cabinet
Imagine you have a personal medical file in a filing cabinet containing:
- Your Name
- Your Address
- Your Private Medical History
- Your Social Security Number

When you send a postcard to a friend, you **do not send your entire filing cabinet**. You only write your name and a short message on a postcard.

### In Code:
- **DTO (Data Transfer Object)** is simply a **lightweight container** used strictly for sending data across the network (between frontend and backend).
- It only contains the specific fields needed for a particular request or response.

### Our DTOs:
1. **`RegisterRequestDto`**: The "envelope" the user sends when signing up:
   - Contains: `FirstName`, `LastName`, `Email`, `Password`, `PhoneNumber`, etc.
2. **`LoginRequestDto`**: The "envelope" the user sends when logging in:
   - Contains only: `Email` and `Password`.
3. **`UserResponseDto`**: The "envelope" the server sends back to the frontend:
   - Contains public info: `Id`, `FirstName`, `LastName`, `Email`, `PhoneNumber`, `CreatedAt`.
   - **Crucial:** It does **NOT** contain `PasswordHash`!

---

## 3. What is a Model (Database Entity)?

A **Model** (in [`Models/User.cs`](file:///c:/Users/ridmi/Desktop/arboveya/backend/Models/User.cs)) represents the **actual table row** inside the PostgreSQL database.

It defines:
- The Table Name (`"Users"`)
- The Columns: `Id`, `FirstName`, `LastName`, `Email`, `PasswordHash`, `Role`, `Address`, `Nationality`, `PhoneNumber`, `CreatedAt`.
- The Primary Key (`Id`).

---

## 4. Why Keep Models and DTOs Separate?

Beginners often ask: *"Why can't I just receive the `User` model directly in the registration endpoint?"*

Here is why that is dangerous:
1. **Security (Over-Posting Vulnerability):**
   If you accepted the `User` model directly, a malicious user could send `"role": "SuperAdmin"` in their JSON and grant themselves admin privileges! With a DTO, you control exactly which fields can be touched.
2. **Privacy:**
   If you return the `User` model directly, your API would send the `PasswordHash` across the internet for anyone to see. With `UserResponseDto`, the password hash never leaves the server.
3. **Validation:**
   A DTO can validate input (e.g., verifying `PhoneNumber` has only numbers and `Password` is at least 6 characters) before any database operations occur.

---

## 5. What is Entity Framework Core (EF Core) & DbContext?

### What is EF Core?
In the old days, developers had to write raw SQL strings like:
```sql
SELECT * FROM Users WHERE Email = 'john@example.com';
```
If you made a typo in the column name, you wouldn't know until the app crashed at runtime.

**Entity Framework Core (EF Core)** is an **ORM (Object-Relational Mapper)**. It lets you write C# code, and it automatically translates that C# into SQL queries for PostgreSQL:
```csharp
// EF Core converts this C# into SELECT * FROM "Users" WHERE "Email" = ...
var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);
```

### What is `AppDbContext`?
The **DbContext** ([`Data/AppDbContext.cs`](file:///c:/Users/ridmi/Desktop/arboveya/backend/Data/AppDbContext.cs)) is the bridge between your C# code and PostgreSQL:
- `DbSet<User> Users`: Represents the `Users` table in C#.
- `OnModelCreating`: Sets database rules (like making `Email` unique so two people cannot register with the same email).

---

## 6. What are Migrations?

Databases change over time (e.g., adding a new column).

A **Migration** is like **Git version control for your database schema**:
1. When you run `dotnet ef migrations add InitialCreate`, EF Core inspects your C# model ([`User.cs`](file:///c:/Users/ridmi/Desktop/arboveya/backend/Models/User.cs)) and creates instructions on how to create the table.
2. When you run `dotnet ef database update`, EF Core connects to PostgreSQL (Neon DB) and runs the SQL commands to create the table.

---

## 7. How Does Password Hashing (BCrypt) Work?

### Encryption vs Hashing:
- **Encryption is two-way:** Data can be encrypted with a key and decrypted back into the original plain text. (If someone steals the key, all passwords are leaked!).
- **Hashing is one-way:** You put plain text into a mathematical formula, and you get an irreversible fingerprint (hash). **It can never be reversed back into the original password.**

### How BCrypt Works:
1. When **John** registers with password `"MyPassword123"`:
   - BCrypt generates a random string called a **Salt**.
   - It hashes `"MyPassword123"` + Salt through 4,096 complex rounds.
   - Output: `$2a$12$eK2s59b8qP1...` (stored in the database).
2. When **John** logs in with `"MyPassword123"`:
   - BCrypt takes the input `"MyPassword123"`, combines it with the stored salt, and runs the same hash.
   - If the resulting hash matches the stored hash, John is authenticated!
   - **Nobody (not even the database administrator) ever knows John's actual password.**

---

## 8. What is JWT (JSON Web Token)?

### Analogy: A Theme Park Wristband
When you pay to enter Disneyland:
1. You show your ID and pay at the ticket booth (Login).
2. The booth hands you a stamped **wristband** (JWT Token).
3. For the rest of the day, when you ride a roller coaster, you **don't show your ID and pay again**. You simply flash your wristband.
4. If someone tries to fake the stamp, the ride operator notices the signature is invalid and denies entry (**401 Unauthorized**).

### Inside a JWT:
A JWT looks like a long string with 3 sections separated by dots:
```
eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTYiLCJlbWFpbCI6InJpZG1pQGV4YW1wbGUuY29tIn0.4vK8mX...
[     HEADER: Algorithm     ] . [     PAYLOAD: User Claims      ] . [       SIGNATURE       ]
```
- **Payload (Claims):** Contains user details like `Id`, `Email`, and `Role`.
- **Signature:** Cryptographically signed using the secret key in `appsettings.json`. The server can verify whether the token was tampered with in under 1 millisecond without even asking the database!

---

## 9. What is a Controller?

The **Controller** ([`Controllers/AuthController.cs`](file:///c:/Users/ridmi/Desktop/arboveya/backend/Controllers/AuthController.cs)) is the **traffic cop** or **front desk** of your API:
- It listens for incoming HTTP requests (e.g., `POST /api/auth/register`, `POST /api/auth/login`, `GET /api/auth/me`).
- It checks if the input is valid (`ModelState.IsValid`).
- It hands the work over to the **Service**.
- It returns an HTTP Status Code:
  - `200 OK`: Success
  - `201 Created`: Successfully registered
  - `400 Bad Request`: Validation error (e.g., letters in phone number)
  - `401 Unauthorized`: Wrong password or missing token
  - `409 Conflict`: Email already taken

---

## 10. What is a Service & Dependency Injection?

### What is a Service?
Instead of putting all your database queries and password hashing inside the Controller, you put that logic into a **Service** ([`Services/AuthService.cs`](file:///c:/Users/ridmi/Desktop/arboveya/backend/Services/AuthService.cs)).
This keeps your code organized, testable, and reusable.

### What is Dependency Injection (DI)?
Instead of a class creating its own database connection with `new AppDbContext()`, the system **injects** it through the class constructor:
```csharp
public AuthService(AppDbContext context, ITokenService tokenService)
{
    _context = context;
    _tokenService = tokenService;
}
```
ASP.NET Core automatically manages the lifetime of these objects.

---

## 11. What is Program.cs?

[`Program.cs`](file:///c:/Users/ridmi/Desktop/arboveya/backend/Program.cs) is the **entry point** and brain of the entire application. It runs first when the app starts:
1. **Reads Settings:** Loads `appsettings.json` (database URL, JWT key).
2. **Registers Services:** Tells .NET which services to make available (Database, Controllers, JWT Authentication, Swagger).
3. **Configures the Middleware Pipeline:** Dictates the journey of every web request:
   ```
   Incoming Request ➔ Exception Handling ➔ Swagger ➔ CORS ➔ Authentication (Check Token) ➔ Authorization (Check Role) ➔ Controller
   ```

---

## 12. What is Swagger UI?

**Swagger UI** (available at `http://localhost:5287/swagger`) is an interactive web page automatically generated from your C# code.
It allows you to:
- See all available endpoints.
- Read documentation and schemas.
- Test endpoints directly in your browser with a visual **Try it out** button without needing to write curl or frontend code.

---

## 13. Step-by-Step Flow: What Happens When Someone Registers & Logs In?

```
=========================
  1. REGISTRATION FLOW
=========================
User in Browser / Swagger
       │  POST /api/auth/register (JSON: name, email, password, phone)
       ▼
AuthController
       │  Validates input (e.g. phone has only digits)
       ▼
AuthService
       │  1. Checks if email already exists in Postgres
       │  2. Hashes password using BCrypt ($2a$12$...)
       │  3. Creates User model & saves to Neon PostgreSQL
       │  4. Calls TokenService to generate a JWT Token
       ▼
AuthController returns: HTTP 201 Created + JWT Token + Sanitized User Profile

=========================
  2. LOGIN FLOW
=========================
User in Browser / Swagger
       │  POST /api/auth/login (JSON: email, password)
       ▼
AuthController
       ▼
AuthService
       │  1. Finds user in database by email
       │  2. Verifies password using BCrypt against stored hash
       │  3. Generates new JWT Token with user claims
       ▼
AuthController returns: HTTP 200 OK + JWT Token

=========================
  3. ACCESSING PROTECTED ROUTE (/api/auth/me)
=========================
User in Browser / Swagger
       │  GET /api/auth/me (Header: Authorization: Bearer <token>)
       ▼
JWT Middleware (Program.cs)
       │  Verifies token signature using your secret key
       │  Extracts user ID from the token claims
       ▼
AuthController
       │  Fetches user from database by ID
       ▼
Returns: HTTP 200 OK + User profile details
```

---

## 14. Folder & File Directory Tour

Here is what every file in your repository does:

```
c:\Users\ridmi\Desktop\arboveya\backend\
├── Controllers/
│   └── AuthController.cs               # Receives HTTP requests for Register, Login, Me
├── Data/
│   └── AppDbContext.cs                 # Entity Framework Core database connection & mappings
├── DTOs/
│   ├── RegisterRequestDto.cs           # Defines what data is required to register
│   ├── LoginRequestDto.cs              # Defines what data is required to login
│   ├── UserResponseDto.cs              # Safe user profile returned to users (no password hash)
│   └── AuthResponseDto.cs              # Bundles JWT token + UserResponseDto together
├── Migrations/
│   ├── 20260912033313_InitialCreate.cs # C# instructions to create the Users table
│   └── InitialCreate.sql               # Pure SQL script of the database schema
├── Models/
│   └── User.cs                         # Database entity representing the "Users" table in Postgres
├── Services/
│   ├── IAuthService.cs                 # Interface (blueprint) for authentication methods
│   ├── AuthService.cs                  # Logic for registering, hashing passwords, logging in
│   ├── ITokenService.cs                # Interface (blueprint) for token creation
│   └── TokenService.cs                 # Logic for generating cryptographic JWT tokens
├── appsettings.json                    # Configuration (Neon DB Connection String, JWT Key)
├── Arboveya.Api.csproj                 # Project dependencies & packages list
├── Arboveya.Api.http                   # Ready-to-click testing file for VS Code / Rider
├── Program.cs                          # Application startup, DI configuration, Swagger setup
└── README.md                           # Quickstart guide
```
