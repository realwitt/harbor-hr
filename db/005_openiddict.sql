-- OpenIddict 7.7.1 EF tables. The owner applies this file. The app role does not.
-- Column names match the EF property names. Payload, client secret, and the JWK set
-- use the names the audit redaction already knows: lower(key) matches the redact list
-- for Payload. ClientSecret and JsonWebKeySet are null on public clients.

create table public."OpenIddictApplications" (
  "Id" text not null,
  "ApplicationType" character varying(50),
  "ClientId" character varying(100),
  "ClientSecret" text,
  "ClientType" character varying(50),
  "ConcurrencyToken" character varying(50),
  "ConsentType" character varying(50),
  "DisplayName" text,
  "DisplayNames" text,
  "JsonWebKeySet" text,
  "Permissions" text,
  "PostLogoutRedirectUris" text,
  "Properties" text,
  "RedirectUris" text,
  "Requirements" text,
  "Settings" text,
  constraint "PK_OpenIddictApplications" primary key ("Id")
);

create unique index "IX_OpenIddictApplications_ClientId"
  on public."OpenIddictApplications" ("ClientId");

create table public."OpenIddictAuthorizations" (
  "Id" text not null,
  "ApplicationId" text,
  "ConcurrencyToken" character varying(50),
  "CreationDate" timestamp with time zone,
  "Properties" text,
  "Scopes" text,
  "Status" character varying(50),
  "Subject" character varying(400),
  "Type" character varying(50),
  constraint "PK_OpenIddictAuthorizations" primary key ("Id"),
  constraint "FK_OpenIddictAuthorizations_OpenIddictApplications_ApplicationId"
    foreign key ("ApplicationId") references public."OpenIddictApplications" ("Id")
);

create index "IX_OpenIddictAuthorizations_ApplicationId_Status_Subject_Type"
  on public."OpenIddictAuthorizations" ("ApplicationId", "Status", "Subject", "Type");

create table public."OpenIddictScopes" (
  "Id" text not null,
  "ConcurrencyToken" character varying(50),
  "Description" text,
  "Descriptions" text,
  "DisplayName" text,
  "DisplayNames" text,
  "Name" character varying(200),
  "Properties" text,
  "Resources" text,
  constraint "PK_OpenIddictScopes" primary key ("Id")
);

create unique index "IX_OpenIddictScopes_Name"
  on public."OpenIddictScopes" ("Name");

create table public."OpenIddictTokens" (
  "Id" text not null,
  "ApplicationId" text,
  "AuthorizationId" text,
  "ConcurrencyToken" character varying(50),
  "CreationDate" timestamp with time zone,
  "ExpirationDate" timestamp with time zone,
  "Payload" text,
  "Properties" text,
  "RedemptionDate" timestamp with time zone,
  "ReferenceId" character varying(100),
  "Status" character varying(50),
  "Subject" character varying(400),
  "Type" character varying(150),
  constraint "PK_OpenIddictTokens" primary key ("Id"),
  constraint "FK_OpenIddictTokens_OpenIddictApplications_ApplicationId"
    foreign key ("ApplicationId") references public."OpenIddictApplications" ("Id"),
  constraint "FK_OpenIddictTokens_OpenIddictAuthorizations_AuthorizationId"
    foreign key ("AuthorizationId") references public."OpenIddictAuthorizations" ("Id")
);

create unique index "IX_OpenIddictTokens_ReferenceId"
  on public."OpenIddictTokens" ("ReferenceId");

create index "IX_OpenIddictTokens_ApplicationId_Status_Subject_Type"
  on public."OpenIddictTokens" ("ApplicationId", "Status", "Subject", "Type");

grant select, insert, update, delete on
  public."OpenIddictApplications",
  public."OpenIddictAuthorizations",
  public."OpenIddictScopes",
  public."OpenIddictTokens"
to harbor_app;

select audit.enable_tracking('public."OpenIddictApplications"'::regclass);
select audit.enable_tracking('public."OpenIddictAuthorizations"'::regclass);
select audit.enable_tracking('public."OpenIddictScopes"'::regclass);
select audit.enable_tracking('public."OpenIddictTokens"'::regclass);
