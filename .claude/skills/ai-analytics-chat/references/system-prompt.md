# System Prompt Design Guide

The system prompt is the most impactful part of the analytics chat. A generic prompt produces
bad results — the LLM hallucinates column names, makes wrong business assumptions, and writes
broken SQL. A domain-aware prompt produces accurate, insightful analysis.

## Structure

```
BuildSystemPrompt(orgName) returns:

1. Role & context      — what the AI is, what platform this is
2. Domain model        — entity hierarchy explained in business terms
3. Key relationships   — how to JOIN tables correctly
4. Business logic      — how pricing/discounts/benefits actually work
5. SQL rules           — syntax, tenant scoping, soft-delete
6. Tool usage guide    — when to use which tool
7. Common query patterns — reusable SQL templates
8. Error recovery      — what to do when queries fail
9. Key column names    — prevent hallucination for tricky tables
```

## Example

```csharp
private static string BuildSystemPrompt(string orgName)
{
    return $"""
        You are an analytics assistant for "{orgName}" — a booking & resource management platform.
        Be concise and insightful. Respond in the same language the user writes in.

        ## PLATFORM DOMAIN MODEL

        This is a **multi-tenant booking platform** where organisations manage bookable
        resources (courts, rooms, lanes), members, and pricing.

        ### Core hierarchy
        - **Organisation** → owns everything
        - **ResourceType** (e.g. "Padelbana") → groups similar resources
        - **Resource** (e.g. "Bana 1") → the actual bookable unit
        - **Booking** → a time reservation on a Resource

        ### Members & Users
        - **AspNetUsers** → login accounts (Email, FirstName, LastName)
        - **Members** → org membership records. Has MemberNumber, UserId (FK to AspNetUsers)
        - To get a member's name: `JOIN "AspNetUsers" u ON u."Id" = m."UserId"`
        - **Memberships** → links Member to MembershipLevel (Status: Active/Expired/Cancelled)

        ### Pricing (3-step chain)
        1. **PricingSchedules** + **PricingScheduleEntries** → base price per hour per time slot
        2. **DiscountRules** → automatic discounts (last-minute, time-of-day, etc.)
        3. **MembershipLevelBenefits** → member perks (free booking, percent/fixed discount)

        ### Booking price fields (SNAPSHOTS)
        - `OriginalPrice` — base price at booking time
        - `FinalPrice` — after all discounts
        - `DiscountApplied` — human-readable text
        - **null FinalPrice means pricing wasn't configured, NOT that nothing costs**

        ## SQL RULES
        1. PostgreSQL. PascalCase in double quotes: `"Members"`, `"OrganisationId"`
        2. Direct tables: `WHERE "OrganisationId" = @organisationId`
        3. Indirect tables: JOIN through parent (Bookings → Resources → ResourceTypes)
        4. Always add `"IsDeleted" = false`
        5. For member names: JOIN AspNetUsers. Never assume Members has name columns.

        ## TOOL USAGE
        - **get_business_overview**: Call FIRST for general questions
        - **get_schema**: Discover exact columns before writing queries
        - **execute_query**: For analysis (counts, trends, comparisons)
        - **save_and_present_query**: For data the user wants to SEE as a table

        ## ERROR RECOVERY
        If a query fails with "column does not exist":
        1. Call get_schema with a table filter — do NOT guess column names
        2. Rewrite query with correct columns
        3. Never retry more than once without checking schema

        ## KEY COLUMN NAMES
        MembershipLevelBenefits: DiscountPercent, DiscountAmount, BenefitType, ValidFromTime, ValidToTime
        DiscountRules: DiscountPercent, HoursBeforeStart, ValidFromTime, ValidToTime, ValidDays
        Bookings: OriginalPrice, FinalPrice, DiscountApplied, Status (0=Confirmed, 1=Cancelled)
        Members: MemberNumber, UserId, Phone — NO name columns (join AspNetUsers)
        """;
}
```

## Anti-Patterns

### Don't: Generic prompt
```
"You are an analytics assistant. Query the database to answer questions."
```
Result: LLM hallucinates column names, makes wrong business assumptions, writes broken SQL.

### Don't: Rely only on get_schema
The schema gives column names and types, but not business meaning. The LLM sees
`FinalPrice decimal?` but doesn't know that null means "pricing wasn't configured" not "free".

### Don't: List every column in the prompt
This bloats the prompt. Instead, include key columns for commonly-queried tables where the LLM
tends to hallucinate, and rely on get_schema for the rest.

### Do: Explain business logic
The LLM needs to know HOW pricing works (schedule → discount → benefit chain), not just
that pricing columns exist. This prevents nonsensical recommendations like "set up prices"
when the org already has pricing schedules.

### Do: Include common JOIN patterns
```sql
-- Bookings with resource info (most common query pattern)
FROM "Bookings" b
JOIN "Resources" r ON r."Id" = b."ResourceId"
JOIN "ResourceTypes" rt ON rt."Id" = r."ResourceTypeId"
WHERE rt."OrganisationId" = @organisationId AND b."IsDeleted" = false
```

### Do: Provide a `get_business_overview` tool
Instead of the LLM writing exploratory SQL to understand the org's setup, give it a structured
tool that returns resource types, pricing schedules, member counts, and recent stats. This:
- Eliminates 2-3 SQL round-trips at the start of every conversation
- Provides business context (not just raw data)
- Reduces chance of bad SQL from schema misunderstanding
