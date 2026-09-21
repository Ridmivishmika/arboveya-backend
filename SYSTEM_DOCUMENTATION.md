# Arboveya System Documentation

This document outlines the system architecture, user roles, pages & access matrix, entity models, and business functionalities for the **Arboveya** e-commerce platform.

---

## 1. System Overview & Technology Stack

- **Backend Architecture**: ASP.NET Core Web API (.NET 10)
- **Object-Relational Mapping (ORM)**: Entity Framework Core 10 (Code-First with Migrations)
- **Database**: PostgreSQL (Hosted on Neon Tech Cloud)
- **Authentication & Security**: JWT (JSON Web Tokens) with BCrypt (Work Factor: 12) password hashing
- **Frontend Architecture**: Next.js 16 (Turbopack, App Router, React Server & Client Components)
- **Styling**: Tailwind CSS v4 with Custom Botanical & Forest Green Design Tokens
- **Payment Gateway Integration**: PayHere Payment Processing API

---

## 2. User Roles & Access Levels

- **Guest (Unregistered User)**:
  - Browse all active products and categories with live product count badges.
  - Filter catalog by category, price slider, and star rating; sort by featured/best-sellers, price, or alphabetical.
  - View rich dynamic product details: photo gallery thumbnails, key botanical benefits (with checkmarks), full ingredients, dosage/directions, customer reviews, and related products.
  - Submit public customer product reviews (ratings & feedback) without mandatory account creation.
  - Add items to the shopping cart, adjust quantities, and proceed to guest checkout.
  - Read approved wellness blog posts, view the company story on the About Us page, and submit Contact Us inquiries.

- **Customer (Registered User)**:
  - All Guest capabilities.
  - User registration and JWT-based authentication (Sign Up / Sign In).
  - Manage personal account profile, including full name, contact phone number, nationality, and default delivery address.
  - View complete order history and real-time order fulfillment status.
  - Submit community wellness blog posts for administrative review and publication.

- **Admin (Store Manager / Operator)**:
  - Full access to the administrative dashboard (`/admin`) and secure operational endpoints.
  - **Dynamic Product Management**: Complete CRUD operations. Configure pricing, stock quantity, packaging weight, category assignment, image uploads & presets, extra gallery image URLs, multiline botanical **Key Benefits** (rendered as checkmarks on the storefront), **Botanical Ingredients**, **How to Use / Dosage** instructions, and **Best Seller** badge toggle. **Zero hardcoded data**.
  - **Category Management**: Create, edit, and delete product categories with image banners and dynamic product counts.
  - **Order Management**: Track customer orders, update fulfillment status (*Pending*, *Processing*, *Shipped*, *Delivered*, *Cancelled*), and monitor payment status.
  - **Blog Moderation**: Author official company articles, edit or delete posts, and review/approve customer blog submissions.
  - **Review Moderation**: Audit and moderate customer product reviews and star ratings.
  - **Site Content Management**: Edit dynamic homepage hero headlines, About Us narrative, and social media connectivity (Facebook URL, WhatsApp contact).
  - **Customer Inquiries Inbox**: Review Contact Us inquiries, update status (*Unread*, *Read*, *Replied*), and record internal admin notes.

---

## 3. Pages & Access Matrix

| Page / Route | Description | Access Level |
| :--- | :--- | :--- |
| **`/` (Home)** | Storefront landing page featuring dynamic hero messaging, curated category highlights, best-seller showcases, customer trust badges, and social media links. | **Public** |
| **`/about`** | Company story, botanical philosophy, sustainable sourcing commitments, and organic quality standards. | **Public** |
| **`/shop`** | Full botanical catalog with dynamic category filters & product counts, price range slider, star rating filter, and sorting. | **Public** |
| **`/shop/[id]`** *(or `/product/[id]`)* | Comprehensive product detail view featuring photo gallery thumbnails, dynamic checkmarked key benefits, tabbed specifications (*Description*, *Ingredients*, *How to Use*, *Reviews*), customer review submission form, and "You May Also Like" related items. | **Public** |
| **`/blog`** | Wellness magazine listing all administrator-approved botanical articles. | **Public** |
| **`/blog/[id]`** | Detailed article view with publication date, author attribution, and full rich text content. | **Public** |
| **`/blog/create`** | Form for registered customers to submit herbal articles for editorial approval. | **Customer** |
| **`/cart`** | Interactive shopping cart displaying line items, quantities, subtotal calculations, and free-shipping progress indicators ($50 threshold). | **Public** |
| **`/checkout`** | Secure checkout process integrated with PayHere payment processing gateway. | **Public / Customer** |
| **`/login`** | Customer and administrator authentication sign-in page. | **Public** |
| **`/signup`** | New customer registration form with input validation. | **Public** |
| **`/profile`** | Customer dashboard to manage shipping address, nationality, phone number, and review past order history. | **Customer** |
| **`/contact`** | Customer inquiry form with subject, email, phone, and message submission. | **Public** |
| **`/admin` (Dashboard)** | High-level business overview presenting total sales, revenue, order counts, product metrics, and recent activity. | **Admin Only** |
| **`/admin/products`** | Full product catalog management interface (table & grid views, search, stock badges) with add/edit modal supporting rich botanical details (benefits, ingredients, how-to-use, gallery images, best seller flag, file uploads). | **Admin Only** |
| **`/admin/categories`** | Category management interface with creation, updates, deletions, and live product counts. | **Admin Only** |
| **`/admin/orders`** | Customer order tracking, status updating, customer details inspection, and payment tracking. | **Admin Only** |
| **`/admin/blog`** | Editorial dashboard to author official posts and moderate user-submitted articles. | **Admin Only** |
| **`/admin/reviews`** | Moderation queue to inspect, approve, or delete customer ratings and reviews. | **Admin Only** |
| **`/admin/content`** | CMS interface to dynamically update homepage hero text, About Us narrative, and official Facebook/WhatsApp contacts. | **Admin Only** |
| **`/admin/messages`** | Customer service inbox to review inquiries submitted via the Contact Us page and record internal follow-up notes. | **Admin Only** |

---

## 4. Entities & Attributes (Database Schema)

The backend is built with **ASP.NET Core (.NET 10)**, **Entity Framework Core**, and **PostgreSQL**. All timestamps are maintained in **UTC**.

### 4.1 User (Customer & Administrator)
- **`Id`** (`UUID / Guid`, Primary Key) — Unique user identifier.
- **`FirstName`** (`VARCHAR(100)`, Required) — User's given name.
- **`LastName`** (`VARCHAR(100)`, Required) — User's surname.
- **`Email`** (`VARCHAR(255)`, Required, Unique) — Login email address.
- **`PasswordHash`** (`TEXT`, Required) — BCrypt-hashed password string.
- **`Role`** (`VARCHAR(50)`, Required) — Authorization role: `"Admin"` or `"Customer"`.
- **`Address`** (`TEXT`, Nullable) — Default shipping street address.
- **`Nationality`** (`VARCHAR(100)`, Nullable) — Customer nationality/country.
- **`PhoneNumber`** (`VARCHAR(30)`, Nullable) — Customer contact telephone number.
- **`CreatedAt`** (`TIMESTAMPTZ`, Required) — Account creation timestamp.

---

### 4.2 Category
- **`Id`** (`UUID / Guid`, Primary Key) — Unique category identifier.
- **`Name`** (`VARCHAR(100)`, Required) — Category name (e.g., *"Herbal Teas"*, *"Supplements"*, *"Skin Care"*).
- **`Description`** (`TEXT`, Nullable) — Category scope and description.
- **`ImageUrl`** (`VARCHAR(500)`, Nullable) — Banner / promotional card image URL.
- **`CreatedAt`** (`TIMESTAMPTZ`, Required) — Record creation timestamp.
- **`UpdatedAt`** (`TIMESTAMPTZ`, Nullable) — Last modification timestamp.
- *(Computed in DTOs)* **`ProductCount`** (`Integer`) — Number of active products belonging to this category.

---

### 4.3 Product *(Updated with Rich Botanical Attributes)*
- **`Id`** (`UUID / Guid`, Primary Key) — Unique product identifier.
- **`CategoryId`** (`UUID / Guid`, Foreign Key -> `Category.Id`, Required) — Associated category.
- **`Name`** (`VARCHAR(200)`, Required) — Product commercial name.
- **`Description`** (`TEXT`, Nullable) — General summary and therapeutic virtues.
- **`Price`** (`DECIMAL(18,2)`, Required) — Retail sales price in USD.
- **`StockQuantity`** (`INTEGER`, Required) — Available inventory count.
- **`ImageUrl`** (`VARCHAR(500)`, Nullable) — Primary product image URL.
- **`Weight`** (`VARCHAR(50)`, Nullable) — Unit size or pack specification (e.g., *"60 Capsules"*, *"100g Loose Leaf"*).
- **`Ingredients`** (`TEXT`, Nullable) — **[NEW]** Full botanical formula, extract ratios, and capsule shell specifications.
- **`HowToUse`** (`TEXT`, Nullable) — **[NEW]** Recommended dosage ritual, preparation temperature, and storage instructions.
- **`KeyBenefits`** (`TEXT`, Nullable) — **[NEW]** Multiline bullet points rendered with custom green checkmarks on the storefront.
- **`GalleryImages`** (`TEXT`, Nullable) — **[NEW]** Comma-separated URLs for secondary view thumbnails.
- **`IsBestSeller`** (`BOOLEAN`, Required, Default: `false`) — **[NEW]** Flag for prominent badges and featured catalog sorting.
- **`CreatedAt`** (`TIMESTAMPTZ`, Required) — Product record creation timestamp.
- **`UpdatedAt`** (`TIMESTAMPTZ`, Nullable) — Last modification timestamp.
- *(Computed in DTOs)* **`AverageRating`** (`Double`) — Computed from approved reviews (default: 5.0).
- *(Computed in DTOs)* **`ReviewCount`** (`Integer`) — Total count of approved reviews.

---

### 4.4 ProductReview
- **`Id`** (`UUID / Guid`, Primary Key) — Unique review identifier.
- **`ProductId`** (`UUID / Guid`, Foreign Key -> `Product.Id`, Required) — Associated product.
- **`UserId`** (`UUID / Guid`, Foreign Key -> `User.Id`, Nullable) — Registered author ID (nullable for public reviews).
- **`AuthorName`** (`VARCHAR(150)`, Nullable) — Display author name for storefront customers.
- **`Rating`** (`INTEGER`, Required, Range: 1-5) — Star rating (1 to 5).
- **`Comment`** (`TEXT`, Nullable) — Customer feedback commentary.
- **`IsApproved`** (`BOOLEAN`, Required, Default: `true` for storefront / modifiable by admin) — Approval status.
- **`CreatedAt`** (`TIMESTAMPTZ`, Required) — Review submission timestamp.
- **`UpdatedAt`** (`TIMESTAMPTZ`, Nullable) — Last moderation timestamp.

---

### 4.5 BlogPost
- **`Id`** (`UUID / Guid`, Primary Key) — Unique post identifier.
- **`AuthorId`** (`UUID / Guid`, Foreign Key -> `User.Id`, Required) — Author user identifier.
- **`Title`** (`VARCHAR(250)`, Required) — Article title.
- **`Content`** (`TEXT`, Required) — Complete article body / markdown.
- **`ImageUrl`** (`VARCHAR(500)`, Nullable) — Header cover photo URL.
- **`IsApproved`** (`BOOLEAN`, Required, Default: `false`) — Editorial approval status.
- **`CreatedAt`** (`TIMESTAMPTZ`, Required) — Submission timestamp.
- **`UpdatedAt`** (`TIMESTAMPTZ`, Nullable) — Last modification timestamp.

---

### 4.6 Order
- **`Id`** (`UUID / Guid`, Primary Key) — Unique order identifier.
- **`UserId`** (`UUID / Guid`, Foreign Key -> `User.Id`, Nullable) — Customer account ID (null for guest checkout).
- **`CustomerName`** (`VARCHAR(150)`, Required) — Full recipient name.
- **`CustomerEmail`** (`VARCHAR(255)`, Required) — Notification email address.
- **`ShippingAddress`** (`TEXT`, Required) — Destination shipping address.
- **`TotalAmount`** (`DECIMAL(18,2)`, Required) — Total order cost including shipping.
- **`PaymentStatus`** (`VARCHAR(50)`, Required, Default: `"Pending"`) — e.g., `"Pending"`, `"Paid"`, `"Failed"`.
- **`OrderStatus`** (`VARCHAR(50)`, Required, Default: `"Pending"`) — e.g., `"Pending"`, `"Processing"`, `"Shipped"`, `"Delivered"`, `"Cancelled"`.
- **`PayHereOrderId`** (`VARCHAR(100)`, Nullable) — Payment reference token from PayHere gateway.
- **`CreatedAt`** (`TIMESTAMPTZ`, Required) — Order placement timestamp.
- **`UpdatedAt`** (`TIMESTAMPTZ`, Nullable) — Last status modification timestamp.

---

### 4.7 OrderItem
- **`Id`** (`UUID / Guid`, Primary Key) — Unique line item identifier.
- **`OrderId`** (`UUID / Guid`, Foreign Key -> `Order.Id`, Required) — Parent order reference.
- **`ProductId`** (`UUID / Guid`, Foreign Key -> `Product.Id`, Required) — Purchased product reference.
- **`Quantity`** (`INTEGER`, Required, Min: 1) — Units purchased.
- **`UnitPrice`** (`DECIMAL(18,2)`, Required) — Unit purchase price at time of order lock.

---

### 4.8 SiteSettings (Dynamic Singleton Content)
- **`Id`** (`INTEGER`, Primary Key, Fixed: `1`) — Singleton identifier.
- **`HomePageHeroText`** (`VARCHAR(300)`, Nullable) — Main headline displayed on the homepage hero banner.
- **`AboutUsContent`** (`TEXT`, Nullable) — Primary company story text for the About Us page.
- **`FacebookLink`** (`VARCHAR(500)`, Nullable) — Official Facebook page URL.
- **`WhatsAppNumber`** (`VARCHAR(50)`, Nullable) — Official WhatsApp direct customer support phone number.
- **`UpdatedAt`** (`TIMESTAMPTZ`, Required) — Last CMS update timestamp.

---

### 4.9 ContactMessage *(Customer Inquiries)*
- **`Id`** (`UUID / Guid`, Primary Key) — Unique inquiry message identifier.
- **`UserId`** (`UUID / Guid`, Foreign Key -> `User.Id`, Nullable) — Registered customer reference if logged in.
- **`Name`** (`VARCHAR(150)`, Required) — Sender's full name.
- **`Email`** (`VARCHAR(255)`, Required) — Sender's email address.
- **`PhoneNumber`** (`VARCHAR(30)`, Nullable) — Optional contact phone number.
- **`Subject`** (`VARCHAR(200)`, Required) — Inquiry subject line.
- **`Message`** (`TEXT`, Required) — Full inquiry content.
- **`Status`** (`VARCHAR(50)`, Required, Default: `"Unread"`) — Status: `"Unread"`, `"Read"`, `"Replied"`.
- **`AdminNotes`** (`TEXT`, Nullable) — Internal resolution or customer service notes.
- **`CreatedAt`** (`TIMESTAMPTZ`, Required) — Submission timestamp.
- **`UpdatedAt`** (`TIMESTAMPTZ`, Nullable) — Last review timestamp.

---

## 5. Summary of Recent Enhancements

1. **Elimination of Hardcoded Product Data**:
   - All product attributes (ingredients, dosage ritual, bullet benefits, gallery views, best seller badges) are now first-class database columns in PostgreSQL.
   - Initialized with realistic Ayurvedic botanical formulas via `DbSeeder.cs` and fully editable from the Admin modal.
2. **Dynamic Shop & Detail Experience**:
   - `/shop` dynamically counts products per category, enables filtering by price range ($0-$100+) and star ratings (2-5 stars), and sorts with best-sellers prioritized.
   - `/shop/[id]` dynamically renders checkmark bullets from `KeyBenefits`, populates distinct **Ingredients** and **How to Use** tabs, displays customer reviews, and loads related items in the same category via `GET /api/products/{id}/related`.
3. **Public Storefront Reviews**:
   - Added endpoint `POST /api/products/{productId}/public-reviews` allowing visitors to submit 1-5 star ratings and reviews directly from the product page.
4. **Admin UI Modal Upgrades**:
   - Added rich inputs for botanical benefits (one per line), ingredients formula, dosage ritual, additional gallery URLs, image uploads with direct preview, and quick botanical presets.
