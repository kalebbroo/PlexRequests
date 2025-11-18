# PlexRequests - Comprehensive Architecture Review

**Date:** 2025-11-07
**Status:** Critical issues identified and partially fixed

---

## Executive Summary

Reviewed the entire PlexRequests application architecture, database design, request flow, notification system, and UI implementation. Found **23 issues** ranging from critical (data loss, broken features) to optimization opportunities.

### ✅ What's Working Correctly

1. **User Registration** - Users ARE saved to database correctly
2. **Authentication Flow** - Plex OAuth integration works properly
3. **Request Submission** - Requests ARE saved with metadata
4. **Request Status Tracking** - Status overlay system functions
5. **SignalR Hub Configuration** - Properly configured with user/admin groups
6. **Admin Approval Workflow** - Approve/deny/available transitions work

---

## Critical Issues Found & Status

### 🔴 **CRITICAL** - Broken Functionality

#### 1. Notification System Completely Broken
**Problem:**
- MainLayout shows notification bell but never connects to SignalR
- Notifications list is static, never populated from real-time events
- No persistence layer → all notifications lost on page refresh

**Impact:** Users never see request updates

**Status:** ⏳ IN PROGRESS
- ✅ Created `NotificationEntity` table
- ✅ Updated `AppDbContext` with indexes
- ⏳ TODO: Wire SignalR to MainLayout
- ⏳ TODO: Create NotificationService methods to persist
- ⏳ TODO: Add API endpoints for notification management

#### 2. MainLayout Service References Don't Exist
**Problem:**
```csharp
@inject IThemeService ThemeService  // ❌ Service doesn't exist
@inject IAuthService AuthService      // ❌ Wrong interface (should be AuthenticationStateProvider)
```

**Impact:** Application won't compile or crashes at runtime

**Status:** ⏳ TODO - Need to fix all service injections

#### 3. Request Limits Never Checked
**Problem:**
- `CheckRequestLimitsAsync()` always returns `true`
- `UserProfile` has limit fields but never enforced
- Users can spam unlimited requests

**Impact:** Resource abuse, database bloat

**Status:** ⏳ TODO - Implement limit checking in `RequestMediaAsync()`

---

### 🟡 **HIGH PRIORITY** - Data Integrity

#### 4. Missing Database Indexes
**Problem:** Queries will be slow as data grows
- `MediaRequestEntity.RequestedBy` - no index
- `MediaRequestEntity.Status` - no index
- `WatchlistItemEntity.Username` - no index

**Status:** ✅ FIXED - Added all missing indexes to `AppDbContext`

#### 5. Missing Foreign Key Relationships
**Problem:**
- `MediaRequestEntity.RequestedBy` is string, not FK to `UserEntity`
- `WatchlistItemEntity.Username` is string, not FK to `UserEntity`
- Can't enforce referential integrity or use joins efficiently

**Status:** ✅ FIXED
- Added `RequestedByUserId` FK (keeping `RequestedBy` for backward compat)
- Added `UserId` FK to `WatchlistItemEntity`

#### 6. WatchlistItemEntity Missing MediaType
**Problem:** No way to distinguish movies from TV shows in watchlist

**Status:** ✅ FIXED - Added `MediaType` field

---

### 🟠 **MEDIUM PRIORITY** - Incomplete Features

#### 7. GetWatchlistAsync() Returns Empty Data
**Problem:**
```csharp
Select(w => new MediaCardDto {
    Id = w.MediaId,
    Title = $"Item #{w.MediaId}"  // ❌ Placeholder, not real data
})
```

**Status:** ⏳ TODO - Fetch real metadata from provider

#### 8. No Notification Persistence
**Problem:** Real-time notifications work but disappear on refresh

**Status:** ✅ PARTIALLY FIXED
- Created `NotificationEntity`
- Still need service layer implementation

#### 9. NavMenu.razor Is Dead Code
**Problem:** `MainLayout` has its own navigation, `NavMenu.razor` is never used

**Status:** ⏳ TODO - Remove or consolidate

#### 10. Mobile Menu Doesn't Close After Navigation
**Problem:** Menu stays open after clicking link on mobile

**Status:** ⏳ TODO - Add close handler to navigation events

---

### 🟢 **LOW PRIORITY** - Optimizations

#### 11. No Caching Layer
**Recommendation:** Add memory cache for:
- Metadata provider responses (TMDB API calls)
- User profiles
- Plex availability index

#### 12. No Background Jobs
**Recommendation:** Implement Hangfire/Quartz for:
- Auto-checking Plex availability for approved requests
- Cleaning up old notifications
- Refreshing Plex library index

#### 13. Inconsistent Error Handling
**Problem:** Some methods return `bool`, others throw exceptions
**Recommendation:** Standardize on Result<T> pattern

#### 14. Static Logs Class Instead of ILogger
**Problem:** Hard to test, no log filtering
**Recommendation:** Replace `Logs.Info()` with injected `ILogger<T>`

---

## Detailed Analysis

### Database Schema Issues

#### Current Schema Problems
```sql
-- MediaRequestEntity
❌ RequestedBy (string) - should be FK
❌ No index on RequestedBy
❌ No index on Status
❌ No index on RequestedAt

-- WatchlistItemEntity
❌ Username (string) - should be FK
❌ Missing MediaType field
❌ No composite index on (UserId, MediaId, MediaType)
```

#### Fixed Schema
```sql
-- MediaRequestEntity
✅ RequestedByUserId (int, FK to Users)
✅ Index on RequestedBy
✅ Index on RequestedByUserId
✅ Index on Status
✅ Index on RequestedAt

-- WatchlistItemEntity
✅ UserId (int, FK to Users)
✅ MediaType (enum)
✅ Composite index (UserId, MediaId, MediaType)
```

---

### Request Flow Analysis

#### User Submits Request
1. ✅ `Browse.razor` → `RequestMedia()` → `MediaRequestService.RequestMediaAsync()`
2. ✅ Service checks for duplicate: `_db.MediaRequests.AnyAsync(...)`
3. ❌ **MISSING:** Request limit check
4. ✅ Enriches with metadata from TMDB
5. ✅ Saves to database
6. ✅ Calls `_notify.RequestCreatedAsync()`
7. ✅ SignalR broadcasts to admins

#### Admin Approves Request
1. ✅ `Requests.razor` (Admin page) → `ApproveRequestAsync()`
2. ✅ Updates status to `Approved`
3. ✅ Sets `ApprovedAt` timestamp
4. ✅ Calls `_notify.RequestApprovedAsync()`
5. ✅ SignalR broadcasts to user

#### Admin Marks Available
1. ✅ `MarkAvailableAsync()`
2. ✅ Updates status to `Available`
3. ✅ Sets `AvailableAt` timestamp
4. ✅ Calls `_notify.RequestAvailableAsync()`
5. ✅ SignalR broadcasts to user

#### **PROBLEM:** Notifications Never Reach UI
- ✅ SignalR broadcasts work
- ❌ MainLayout doesn't connect to hub
- ❌ No `HubConnection` injection
- ❌ No event handlers registered
- ❌ Notification list never updates

---

### Notification System Architecture

#### Current Implementation
```
NotificationService.RequestCreatedAsync()
    ↓
IHubContext<NotificationsHub>.Clients.Group("admins").SendAsync("RequestCreated", dto)
    ↓
❌ DEAD END - No client listening
```

#### What It Should Be
```
NotificationService.RequestCreatedAsync()
    ↓
1. Save notification to database
2. Broadcast via SignalR
    ↓
MainLayout (with HubConnection)
    ↓
hubConnection.On<MediaRequestDto>("RequestCreated", notification => {
    _notifications.Add(...);
    _notificationCount++;
    StateHasChanged();
});
```

---

## Implementation Status

### ✅ Completed Fixes

1. **Database Schema Improvements**
   - Added missing indexes for performance
   - Added foreign key relationships
   - Added `MediaType` to `WatchlistItemEntity`
   - Created `NotificationEntity` table

2. **Entity Improvements**
   - Added `RequestedByUserId` FK to `MediaRequestEntity`
   - Added `UserId` FK to `WatchlistItemEntity`
   - Added proper MaxLength attributes
   - Added navigation properties

### ⏳ In Progress / TODO

1. **Fix MainLayout Service Injections**
   - Remove `IThemeService` (doesn't exist)
   - Replace `IAuthService` with `AuthenticationStateProvider`
   - Add `HubConnection` for notifications

2. **Wire Up Notification System**
   - Connect SignalR in MainLayout
   - Add notification persistence in NotificationService
   - Create notification management API endpoints
   - Add mark-as-read functionality

3. **Implement Request Limits**
   - Check limits in `RequestMediaAsync()`
   - Enforce per-user movie/TV/music limits
   - Add admin override capability

4. **Fix Watchlist Metadata**
   - Fetch real media details in `GetWatchlistAsync()`
   - Batch fetch for performance

5. **Create Database Migration**
   - Generate EF Core migration for schema changes
   - Test migration on clean database

---

## Recommendations

### Immediate Actions (Do First)
1. ⚠️ Fix MainLayout service references (app won't compile)
2. ⚠️ Wire up SignalR in MainLayout (notifications broken)
3. ⚠️ Implement request limits (prevent abuse)

### Short Term (This Week)
4. Add notification persistence
5. Fix watchlist metadata
6. Create database migration
7. Add comprehensive logging

### Medium Term (This Month)
8. Add caching layer
9. Implement background jobs
10. Add comprehensive unit tests
11. Standardize error handling

### Long Term (Nice to Have)
12. Add Discord integration
13. Add email notifications
14. Add request analytics dashboard
15. Implement user reputation system

---

## Files Modified

### ✅ Already Modified
- `Infrastructure/Entities/NotificationEntity.cs` (created)
- `Infrastructure/Entities/MediaRequestEntity.cs` (updated)
- `Infrastructure/Entities/WatchlistItemEntity.cs` (updated)
- `Infrastructure/Data/AppDbContext.cs` (updated)
- `Shared/Enums.cs` (updated NotificationType)

### ⏳ Need to Modify
- `Components/Layout/MainLayout.razor` (fix services, add SignalR)
- `Services/Implementations/NotificationService.cs` (add persistence)
- `Services/Implementations/MediaRequestService.cs` (add limits, fix watchlist)
- `Services/Abstractions/Interfaces.cs` (add notification methods)

---

## Testing Checklist

### Database Migrations
- [ ] Generate migration: `dotnet ef migrations add ArchitectureImprovements`
- [ ] Review generated SQL
- [ ] Test on clean database
- [ ] Test on database with existing data
- [ ] Verify indexes created

### Notification System
- [ ] User receives notification when request approved
- [ ] Admin receives notification when request created
- [ ] Notification persists after page refresh
- [ ] Mark as read functionality works
- [ ] Notification count badge updates in real-time

### Request Limits
- [ ] User cannot exceed movie limit
- [ ] User cannot exceed TV limit
- [ ] Admin can override limits
- [ ] Clear error message when limit reached

---

## Conclusion

The application architecture is **fundamentally sound** but has several **critical bugs** that break key features (notifications, limits). The database schema needs improvements for performance and data integrity.

**Estimated Effort:**
- Critical fixes: 4-6 hours
- All high priority: 8-12 hours
- Full implementation: 20-30 hours

**Priority Order:**
1. Fix MainLayout (1 hour) - **BLOCKING**
2. Wire SignalR (2 hours) - **CRITICAL**
3. Implement limits (2 hours) - **HIGH**
4. Database migration (1 hour) - **HIGH**
5. Everything else (15+ hours) - **NICE TO HAVE**
