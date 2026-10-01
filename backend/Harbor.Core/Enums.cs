namespace Harbor;

public enum EmployeeRole
{
    Employee,
    HrAdmin,
}

public enum GrantModel
{
    Accrued,
    Instant,
    Unpaid,
}

public enum LeaveStatus
{
    Pending,
    Approved,
    Denied,
    Cancelled,
}

public enum LedgerKind
{
    Grant,
    Accrual,
    Usage,
    Release,
    Adjustment,
    Expiration,
}

public enum DeductionKind
{
    Hsa,
    HealthFsa,
    DependentCareFsa,
}

public enum ElectionStatus
{
    PendingConfirm,
    Active,
    Ended,
}

public enum QuoteKind
{
    Leave,
    Deduction,
}

public enum HsaCoverage
{
    Self,
    Family,
}
