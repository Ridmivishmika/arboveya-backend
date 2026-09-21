# Arboveya Backend API (.NET & PostgreSQL)

ASP.NET Core Web API with PostgreSQL database integration via Entity Framework Core, providing secure user authentication (Register & Login) with BCrypt password hashing and JWT (JSON Web Token) bearer tokens.

---

## 📋 Features & Model Specifications

The `User` entity contains all required fields:
- **`Id`** (`Guid` / UUID, Primary Key)
- **`FirstName`** (`string`, max 100)
- **`LastName`** (`string`, max 100)
- **`Email`** (`string`, unique index)
- **`PasswordHash`** (`string`, BCrypt hash)
- **`Role`** (`string`, e.g. `"Customer"`, `"Admin"`)
- **`Address`** (`string` / text)
- **`Nationality`** (`string`, max 100)
- **`PhoneNumber`** (`string`, max 30)
- **`CreatedAt`** (`DateTime`, UTC timestamp)

---

## ⚙️ Configuration

Open `appsettings.json` and adjust your PostgreSQL database credentials:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=arboveya_db;Username=postgres;Password=postgres"
  },
  "Jwt": {
    "Key": "arboveya_super_secret_jwt_key_with_at_least_256_bits_length_12345!",
    "Issuer": "ArboveyaApi",
    "Audience": "ArboveyaClient",
    "ExpiryMinutes": 1440
  }
}
```

---

## 🚀 Getting Started

### 1. Run Database Migrations

You can apply the database migrations directly using the .NET CLI:
```bash
dotnet ef database update
```

*(Alternatively, run the pre-generated SQL script in [`Migrations/InitialCreate.sql`](file:///c:/Users/ridmi/Desktop/arboveya/backend/Migrations/InitialCreate.sql) using pgAdmin or psql).*

### 2. Run the Application

```bash
dotnet run
```

The API starts on: `http://localhost:5287`

### 3. Interactive API Documentation (Swagger)

Navigate to:
- **Swagger UI**: [http://localhost:5287/swagger](http://localhost:5287/swagger)

You can test all endpoints directly in the browser and use the **Authorize** button with the JWT token returned from login/registration.

---

## 📡 API Endpoints

### 1. Register User
- **Method:** `POST`
- **URL:** `/api/auth/register`
- **Request Body:**
```json
{
  "firstName": "John",
  "lastName": "Doe",
  "email": "john.doe@example.com",
  "password": "SecurePassword123!",
  "role": "Customer",
  "address": "123 Main Street, Colombo",
  "nationality": "Sri Lankan",
  "phoneNumber": "+94771234567"
}
```
- **Response (201 Created):**
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "tokenType": "Bearer",
  "expiresAt": "2026-09-13T08:50:00Z",
  "user": {
    "id": "e98e29a9-30df-4ad4-9a84-0a3597d3eb89",
    "firstName": "John",
    "lastName": "Doe",
    "fullName": "John Doe",
    "email": "john.doe@example.com",
    "role": "Customer",
    "address": "123 Main Street, Colombo",
    "nationality": "Sri Lankan",
    "phoneNumber": "+94771234567",
    "createdAt": "2026-09-12T08:50:00Z"
  }
}
```

---

### 2. Login User
- **Method:** `POST`
- **URL:** `/api/auth/login`
- **Request Body:**
```json
{
  "email": "john.doe@example.com",
  "password": "SecurePassword123!"
}
```
- **Response (200 OK):**
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "tokenType": "Bearer",
  "expiresAt": "2026-09-13T08:50:00Z",
  "user": { ... }
}
```

---

### 3. Get Current User Profile (Protected)
- **Method:** `GET`
- **URL:** `/api/auth/me`
- **Headers:** `Authorization: Bearer <your_jwt_token>`
- **Response (200 OK):**
```json
{
  "id": "e98e29a9-30df-4ad4-9a84-0a3597d3eb89",
  "firstName": "John",
  "lastName": "Doe",
  "fullName": "John Doe",
  "email": "john.doe@example.com",
  "role": "Customer",
  "address": "123 Main Street, Colombo",
  "nationality": "Sri Lankan",
  "phoneNumber": "+94771234567",
  "createdAt": "2026-09-12T08:50:00Z"
}
```
