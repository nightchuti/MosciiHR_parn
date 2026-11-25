using System.Data;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;

public static class BookingApi
{
    private static readonly HttpClient LineClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/booking");
        api.AddEndpointFilter(async (context, next) =>
        {
            var environment = context.HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>();
            return environment.IsDevelopment()
                ? await next(context)
                : Results.NotFound();
        });

        api.MapGet("/demo-users", async (IConfiguration config, CancellationToken ct) =>
        {
            try
            {
                await using var db = await Open(config, ct);
                await using var cmd = new SqlCommand("""
                    SELECT e.EmployeeId, e.DisplayName, e.EmployeeCode,
                           CONVERT(bit, CASE WHEN EXISTS (
                               SELECT 1 FROM hr.EmployeeRole er
                               INNER JOIN hr.Role r ON r.RoleId = er.RoleId
                               WHERE er.EmployeeId = e.EmployeeId AND r.Code = 'FleetAdmin'
                           ) THEN 1 ELSE 0 END) AS IsFleetAdmin
                    FROM hr.Employee e
                    WHERE e.Status = 'Active'
                    ORDER BY e.EmployeeId
                    """, db);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                var rows = new List<object>();
                while (await reader.ReadAsync(ct))
                    rows.Add(new { employeeId = reader.GetInt32(0), name = reader.GetString(1), employeeCode = reader.GetString(2), isFleetAdmin = reader.GetBoolean(3) });
                return Results.Ok(rows);
            }
            catch (Exception ex) { return Error(ex); }
        });

        api.MapGet("/snapshot", async (int actorId, IConfiguration config, CancellationToken ct) =>
        {
            if (actorId <= 0) return Results.BadRequest(new { message = "à¸£à¸°à¸šà¸¸à¸œà¸¹à¹‰à¹ƒà¸Šà¹‰à¸à¹ˆà¸­à¸™à¹€à¸›à¸´à¸”à¸›à¸à¸´à¸—à¸´à¸™" });
            try
            {
                await ExpirePendingBookings(config, ct);
                await using var db = await Open(config, ct);
                bool isFleetAdmin;
                await using (var role = new SqlCommand("""
                    SELECT CONVERT(bit, CASE WHEN EXISTS (
                        SELECT 1 FROM hr.EmployeeRole er JOIN hr.Role r ON r.RoleId = er.RoleId
                        WHERE er.EmployeeId = @ActorId AND r.Code = 'FleetAdmin'
                    ) THEN 1 ELSE 0 END)
                    """, db))
                {
                    role.Parameters.AddWithValue("@ActorId", actorId);
                    isFleetAdmin = Convert.ToBoolean(await role.ExecuteScalarAsync(ct));
                }
                var vehicles = await ReadRows(db, "SELECT VehicleId, PlateNo, Name, Seats, Status FROM hr.Vehicle ORDER BY Status, PlateNo", ct);
                var bookings = await ReadRows(db, """
                    SELECT b.BookingId, b.BookingNo, b.VehicleId, v.PlateNo, v.Name AS VehicleName,
                           b.EmployeeId, e.DisplayName AS EmployeeName, b.StartAt, b.EndAt, b.Destination,
                           b.Purpose, b.Status, b.Channel, b.CreatedAt, b.StatusReason, b.StatusChangedAt, b.ExpiresAt,
                           (SELECT COUNT(*) FROM hr.Booking other WHERE other.VehicleId=b.VehicleId AND other.BookingId<>b.BookingId
                               AND other.Status='Pending' AND other.ExpiresAt>SYSDATETIMEOFFSET()
                               AND other.StartAt<b.EndAt AND other.EndAt>b.StartAt) AS PendingConflictCount
                    FROM hr.Booking b
                    INNER JOIN hr.Vehicle v ON v.VehicleId = b.VehicleId
                    INNER JOIN hr.Employee e ON e.EmployeeId = b.EmployeeId
                    WHERE @IsFleetAdmin = 1 OR b.EmployeeId = @ActorId
                    ORDER BY b.StartAt DESC
                    """, ct, ("@ActorId", actorId), ("@IsFleetAdmin", isFleetAdmin));
                var bookingHistory = await ReadRows(db, """
                    SELECT h.BookingId, h.Status, h.ActorEmployeeId, actor.DisplayName AS ActorName, h.Reason, h.ChangedAt
                    FROM hr.BookingStatusHistory h
                    JOIN hr.Booking b ON b.BookingId=h.BookingId
                    JOIN hr.Employee actor ON actor.EmployeeId=h.ActorEmployeeId
                    WHERE @IsFleetAdmin=1 OR b.EmployeeId=@ActorId
                    ORDER BY h.ChangedAt DESC
                    """, ct, ("@ActorId", actorId), ("@IsFleetAdmin", isFleetAdmin));
                var bookingConflicts = await ReadRows(db, """
                    SELECT target.BookingId, other.BookingId AS OtherBookingId, other.BookingNo AS OtherBookingNo,
                           requester.DisplayName AS OtherEmployeeName, other.StartAt, other.EndAt
                    FROM hr.Booking target
                    JOIN hr.Booking other ON other.VehicleId=target.VehicleId AND other.BookingId<>target.BookingId
                    JOIN hr.Employee requester ON requester.EmployeeId=other.EmployeeId
                    WHERE @IsFleetAdmin=1 AND target.Status='Pending' AND other.Status='Pending'
                      AND target.ExpiresAt>SYSDATETIMEOFFSET() AND other.ExpiresAt>SYSDATETIMEOFFSET()
                      AND other.StartAt<target.EndAt AND other.EndAt>target.StartAt
                    ORDER BY target.BookingId, other.CreatedAt
                    """, ct, ("@IsFleetAdmin", isFleetAdmin));
                var calendarBookings = await ReadRows(db, """
                    SELECT b.BookingId, b.VehicleId, v.PlateNo, b.StartAt, b.EndAt, b.Status
                    FROM hr.Booking b
                    INNER JOIN hr.Vehicle v ON v.VehicleId = b.VehicleId
                    WHERE b.Status IN ('Pending', 'Confirmed')
                    ORDER BY b.StartAt
                    """, ct);
                var calendarDetails = await ReadRows(db, """
                    SELECT b.BookingId, b.EmployeeId, e.DisplayName AS EmployeeName, b.Destination, b.Purpose
                    FROM hr.Booking b
                    INNER JOIN hr.Employee e ON e.EmployeeId = b.EmployeeId
                    WHERE b.Status IN ('Pending', 'Confirmed')
                      AND (@IsFleetAdmin = 1 OR b.EmployeeId = @ActorId)
                    """, ct, ("@ActorId", actorId), ("@IsFleetAdmin", isFleetAdmin));
                var blackouts = await ReadRows(db, """
                    SELECT x.VehicleBlackoutId, x.VehicleId, v.PlateNo, v.Name AS VehicleName,
                           x.StartAt, x.EndAt, x.Reason
                    FROM hr.VehicleBlackout x INNER JOIN hr.Vehicle v ON v.VehicleId = x.VehicleId
                    ORDER BY x.StartAt DESC
                    """, ct);
                return Results.Ok(new { vehicles, bookings, bookingHistory, bookingConflicts, calendarBookings, calendarDetails, blackouts });
            }
            catch (Exception ex) { return Error(ex); }
        });

        api.MapGet("/availability", async (DateTimeOffset startAt, DateTimeOffset endAt, IConfiguration config, CancellationToken ct) =>
        {
            if (startAt <= DateTimeOffset.UtcNow) return Results.BadRequest(new { message = "เวลาออกเดินทางต้องอยู่ในอนาคต" });
            if (startAt >= endAt) return Results.BadRequest(new { message = "เวลาออกต้องอยู่ก่อนเวลาคืนรถ" });
            try
            {
                await ExpirePendingBookings(config, ct);
                await using var db = await Open(config, ct);
                var rows = await ReadRows(db, """
                    SELECT v.VehicleId, v.PlateNo, v.Name, v.Seats
                    FROM hr.Vehicle v
                    WHERE v.Status = 'Active'
                      AND NOT EXISTS (SELECT 1 FROM hr.VehicleBlackout x WHERE x.VehicleId = v.VehicleId AND x.StartAt < @EndAt AND x.EndAt > @StartAt)
                      AND NOT EXISTS (SELECT 1 FROM hr.Booking b WHERE b.VehicleId = v.VehicleId
                          AND (b.Status='Confirmed' OR (b.Status='Pending' AND b.ExpiresAt>SYSDATETIMEOFFSET()))
                          AND b.StartAt < @EndAt AND b.EndAt > @StartAt)
                    ORDER BY v.PlateNo
                    """, ct, ("@StartAt", startAt), ("@EndAt", endAt));
                return Results.Ok(rows);
            }
            catch (Exception ex) { return Error(ex); }
        });

        api.MapPost("", async (CreateBooking request, IConfiguration config, CancellationToken ct) =>
        {
            if (request.EmployeeId <= 0 || request.VehicleId <= 0 || request.StartAt <= DateTimeOffset.UtcNow || request.StartAt >= request.EndAt ||
                string.IsNullOrWhiteSpace(request.Destination) || string.IsNullOrWhiteSpace(request.Purpose) ||
                string.IsNullOrWhiteSpace(request.RequestKey))
                return Results.BadRequest(new { message = "กรุณาตรวจข้อมูลให้ครบ และกำหนดเวลาออกเดินทางในอนาคตซึ่งต้องก่อนเวลาคืนรถ" });
            if (request.Destination.Length > 300 || request.Purpose.Length > 1000 || request.RequestKey.Length > 200)
                return Results.BadRequest(new { message = "ข้อมูลบางช่องยาวเกินกำหนด" });

            try
            {
                await ExpirePendingBookings(config, ct);
                await using var db = await Open(config, ct);
                await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                await using (var prior = new SqlCommand("SELECT BookingId, BookingNo, Status, EmployeeId FROM hr.Booking WITH (UPDLOCK, HOLDLOCK) WHERE RequestKey = @Key", db, tx))
                {
                    prior.Parameters.AddWithValue("@Key", request.RequestKey);
                    await using var reader = await prior.ExecuteReaderAsync(ct);
                    if (await reader.ReadAsync(ct))
                    {
                        if (reader.GetInt32(3) != request.EmployeeId)
                        {
                            await reader.DisposeAsync();
                            await tx.RollbackAsync(ct);
                            return Results.Conflict(new { message = "คีย์คำขอนี้ถูกใช้กับผู้จองรายอื่นแล้ว" });
                        }
                        var existing = new { bookingId = reader.GetInt32(0), bookingNo = reader.GetString(1), status = reader.GetString(2), duplicate = true };
                        await reader.DisposeAsync();
                        await tx.CommitAsync(ct);
                        return Results.Ok(existing);
                    }
                }

                await using (var employee = new SqlCommand("SELECT 1 FROM hr.Employee WHERE EmployeeId = @Id AND Status = 'Active'", db, tx))
                {
                    employee.Parameters.AddWithValue("@Id", request.EmployeeId);
                    if (await employee.ExecuteScalarAsync(ct) is null)
                    {
                        await tx.RollbackAsync(ct);
                        return Results.BadRequest(new { message = "ไม่พบพนักงานตัวอย่างที่ใช้งานได้ กรุณาเลือกผู้จองใหม่" });
                    }
                }

                string? vehicleStatus;
                await using (var vehicle = new SqlCommand("SELECT Status FROM hr.Vehicle WITH (UPDLOCK, HOLDLOCK) WHERE VehicleId = @Id", db, tx))
                {
                    vehicle.Parameters.AddWithValue("@Id", request.VehicleId);
                    vehicleStatus = (string?)await vehicle.ExecuteScalarAsync(ct);
                }
                if (vehicleStatus != "Active")
                {
                    await tx.RollbackAsync(ct);
                    return Results.Conflict(new { code = "VEHICLE_INACTIVE", message = "รถคันนี้ปิดใช้งานหรือไม่พบรถ กรุณาเลือกรถคันอื่น" });
                }

                await using (var overlap = new SqlCommand("""
                    SELECT
                        CASE WHEN EXISTS (SELECT 1 FROM hr.VehicleBlackout WHERE VehicleId=@VehicleId AND StartAt < @EndAt AND EndAt > @StartAt) THEN 1
                             WHEN EXISTS (SELECT 1 FROM hr.Booking WHERE VehicleId=@VehicleId
                                 AND (Status='Confirmed' OR (Status='Pending' AND ExpiresAt>SYSDATETIMEOFFSET()))
                                 AND StartAt < @EndAt AND EndAt > @StartAt) THEN 2
                             ELSE 0 END
                    """, db, tx))
                {
                    overlap.Parameters.AddWithValue("@VehicleId", request.VehicleId);
                    overlap.Parameters.AddWithValue("@StartAt", request.StartAt);
                    overlap.Parameters.AddWithValue("@EndAt", request.EndAt);
                    var conflict = Convert.ToInt32(await overlap.ExecuteScalarAsync(ct));
                    if (conflict != 0)
                    {
                        await tx.RollbackAsync(ct);
                        return Results.Conflict(new { code = conflict == 1 ? "BLACKOUT" : "BOOKED", message = conflict == 1 ? "รถอยู่ในช่วงปิดซ่อมบำรุง" : "รถถูกจองในช่วงเวลาดังกล่าวแล้ว กรุณาเลือกเวลา/รถใหม่" });
                    }
                }

                var bookingNo = "BK-" + DateTime.UtcNow.ToString("yyMMdd", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
                int bookingId;
                await using (var insert = new SqlCommand("""
                    DECLARE @CreatedAt datetimeoffset = SYSDATETIMEOFFSET();
                    INSERT INTO hr.Booking (BookingNo, VehicleId, EmployeeId, StartAt, EndAt, Destination, Purpose,
                        Status, Channel, RequestKey, StatusReason, StatusChangedById, StatusChangedAt, ExpiresAt, CreatedAt)
                    OUTPUT INSERTED.BookingId
                    VALUES (@BookingNo, @VehicleId, @EmployeeId, @StartAt, @EndAt, @Destination, @Purpose,
                        'Pending', @Channel, @RequestKey, NULL, @EmployeeId, SYSDATETIMEOFFSET(),
                        CASE WHEN DATEADD(HOUR, 3, @CreatedAt) < @StartAt
                             THEN DATEADD(HOUR, 3, @CreatedAt) ELSE @StartAt END, @CreatedAt)
                    """, db, tx))
                {
                    insert.Parameters.AddWithValue("@BookingNo", bookingNo);
                    insert.Parameters.AddWithValue("@VehicleId", request.VehicleId);
                    insert.Parameters.AddWithValue("@EmployeeId", request.EmployeeId);
                    insert.Parameters.AddWithValue("@StartAt", request.StartAt);
                    insert.Parameters.AddWithValue("@EndAt", request.EndAt);
                    insert.Parameters.AddWithValue("@Destination", request.Destination.Trim());
                    insert.Parameters.AddWithValue("@Purpose", request.Purpose.Trim());
                    insert.Parameters.AddWithValue("@Channel", "Web");
                    insert.Parameters.AddWithValue("@RequestKey", request.RequestKey);
                    bookingId = Convert.ToInt32(await insert.ExecuteScalarAsync(ct));
                }
                await AddBookingHistory(db, tx, bookingId, "Pending", request.EmployeeId, "ส่งคำขอจอง", ct);
                await tx.CommitAsync(ct);
                return Results.Created($"/api/booking/{bookingId}", new { bookingId, bookingNo, status = "Pending", message = "ส่งคำขอแล้ว รอผู้ดูแลรถอนุมัติ" });
            }
            catch (Exception ex) { return Error(ex); }
        });

        api.MapPatch("/{bookingId:int}/status", async (int bookingId, ChangeStatus request, IConfiguration config, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("BookingApi");
            var reason = request.Reason?.Trim();
            var requiresReason = request.Status is "Rejected" or "Cancelled";
            if (request.ActorId <= 0 || request.Status is not ("Confirmed" or "Rejected" or "Cancelled") ||
                (requiresReason && string.IsNullOrWhiteSpace(reason)) || (reason?.Length ?? 0) > 1000)
                return Results.BadRequest(new { message = "ตรวจสอบสถานะและเหตุผล (กรณีปฏิเสธหรือยกเลิกต้องระบุเหตุผล ไม่เกิน 1,000 ตัวอักษร)" });
            try
            {
                await ExpirePendingBookings(config, ct);
                await using var db = await Open(config, ct);
                await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                bool actorIsFleetAdmin;
                await using (var role = new SqlCommand("SELECT CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM hr.EmployeeRole er JOIN hr.Role r ON r.RoleId=er.RoleId WHERE er.EmployeeId=@ActorId AND r.Code='FleetAdmin') THEN 1 ELSE 0 END)", db, tx))
                {
                    role.Parameters.AddWithValue("@ActorId", request.ActorId);
                    actorIsFleetAdmin = Convert.ToBoolean(await role.ExecuteScalarAsync(ct));
                }
                int vehicleId, employeeId;
                DateTimeOffset startAt;
                string current, bookingNo;
                await using (var lookup = new SqlCommand("SELECT VehicleId, BookingNo FROM hr.Booking WHERE BookingId=@Id", db, tx))
                {
                    lookup.Parameters.AddWithValue("@Id", bookingId);
                    await using var reader = await lookup.ExecuteReaderAsync(ct);
                    if (!await reader.ReadAsync(ct)) { await reader.DisposeAsync(); await tx.RollbackAsync(ct); return Results.NotFound(new { message = "ไม่พบรายการจอง" }); }
                    vehicleId = reader.GetInt32(0); bookingNo = reader.GetString(1);
                }
                await using (var vehicleLock = new SqlCommand("SELECT Status FROM hr.Vehicle WITH (UPDLOCK,HOLDLOCK) WHERE VehicleId=@Id", db, tx))
                {
                    vehicleLock.Parameters.AddWithValue("@Id", vehicleId);
                    if (await vehicleLock.ExecuteScalarAsync(ct) is null) { await tx.RollbackAsync(ct); return Results.Conflict(new { message = "ไม่พบรถของรายการจอง" }); }
                }
                await using (var cmd = new SqlCommand("SELECT EmployeeId, Status, StartAt FROM hr.Booking WITH (UPDLOCK, HOLDLOCK) WHERE BookingId=@Id", db, tx))
                {
                    cmd.Parameters.AddWithValue("@Id", bookingId);
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    if (!await reader.ReadAsync(ct)) { await reader.DisposeAsync(); await tx.RollbackAsync(ct); return Results.NotFound(new { message = "ไม่พบรายการจอง" }); }
                    employeeId = reader.GetInt32(0); current = reader.GetString(1); startAt = reader.GetDateTimeOffset(2);
                }
                var allowed = current == "Pending" || (current == "Confirmed" && request.Status == "Cancelled");
                if (!allowed)
                {
                    await tx.RollbackAsync(ct);
                    return Results.Conflict(new { message = "เปลี่ยนสถานะนี้ไม่ได้ รายการอาจถูกดำเนินการไปแล้ว" });
                }
                if (!actorIsFleetAdmin && (request.Status != "Cancelled" || request.ActorId != employeeId))
                {
                    await tx.RollbackAsync(ct);
                    return Results.Json(new { message = "ผู้ใช้ตัวอย่างนี้ไม่มีสิทธิ์เปลี่ยนสถานะรายการดังกล่าว" }, statusCode: 403);
                }
                if (request.Status == "Cancelled" && startAt <= DateTimeOffset.UtcNow)
                {
                    await tx.RollbackAsync(ct);
                    return Results.Conflict(new { message = "ยกเลิกได้เฉพาะก่อนเวลาออกเดินทาง" });
                }
                await using (var vehicleLock = new SqlCommand("SELECT Status FROM hr.Vehicle WHERE VehicleId=@Id", db, tx))
                {
                    vehicleLock.Parameters.AddWithValue("@Id", vehicleId);
                    if ((string?)await vehicleLock.ExecuteScalarAsync(ct) != "Active" && request.Status == "Confirmed")
                    {
                        await tx.RollbackAsync(ct);
                        return Results.Conflict(new { message = "อนุมัติไม่ได้ เพราะรถปิดใช้งานแล้ว" });
                    }
                }
                if (request.Status == "Confirmed")
                {
                    await using var check = new SqlCommand("""
                        SELECT CASE WHEN EXISTS (SELECT 1 FROM hr.VehicleBlackout x JOIN hr.Booking b ON b.VehicleId=x.VehicleId WHERE b.BookingId=@Id AND x.StartAt < b.EndAt AND x.EndAt > b.StartAt)
                                    OR EXISTS (SELECT 1 FROM hr.Booking other JOIN hr.Booking target ON target.VehicleId=other.VehicleId WHERE target.BookingId=@Id AND other.BookingId<>target.BookingId AND other.Status='Confirmed' AND other.StartAt<target.EndAt AND other.EndAt>target.StartAt)
                                    OR EXISTS (SELECT 1 FROM hr.Vehicle v JOIN hr.Booking b ON b.VehicleId=v.VehicleId WHERE b.BookingId=@Id AND v.Status<>'Active') THEN 1 ELSE 0 END
                        """, db, tx);
                    check.Parameters.AddWithValue("@Id", bookingId);
                    if (Convert.ToInt32(await check.ExecuteScalarAsync(ct)) != 0)
                    {
                        await tx.RollbackAsync(ct);
                        return Results.Conflict(new { message = "อนุมัติไม่ได้ เนื่องจากรถถูกจอง/ปิดซ่อม/ปิดใช้งานในช่วงนี้แล้ว" });
                    }
                }
                await using (var update = new SqlCommand("UPDATE hr.Booking SET Status=@Status, StatusReason=@Reason, StatusChangedById=@ActorId, StatusChangedAt=SYSDATETIMEOFFSET() WHERE BookingId=@Id AND (Status <> 'Pending' OR ExpiresAt > SYSDATETIMEOFFSET())", db, tx))
                {
                    update.Parameters.AddWithValue("@Status", request.Status);
                    update.Parameters.Add("@Reason", SqlDbType.NVarChar, 1000).Value = (object?)reason ?? DBNull.Value;
                    update.Parameters.AddWithValue("@ActorId", request.ActorId);
                    update.Parameters.AddWithValue("@Id", bookingId);
                    if (await update.ExecuteNonQueryAsync(ct) == 0)
                    {
                        await tx.RollbackAsync(ct);
                        await ExpirePendingBookings(config, ct);
                        return Results.Conflict(new { message = "คำขอหมดอายุแล้ว จึงดำเนินการต่อไม่ได้" });
                    }
                }
                await AddBookingHistory(db, tx, bookingId, request.Status, request.ActorId,
                    request.Status == "Confirmed" ? "อนุมัติคำขอ" : reason!, ct);
                var cancelledRequests = new List<AffectedBooking>();
                var alternativeVehicles = new List<Dictionary<string, object?>>();
                if (request.Status == "Confirmed")
                {
                    var overlapReason = $"ยกเลิกเนื่องจากคำขอ {bookingNo} ได้รับอนุมัติและช่วงเวลาเดินทางทับซ้อนกัน";
                    await using (var cancelOverlaps = new SqlCommand("""
                        UPDATE other
                        SET Status='Cancelled', StatusReason=@Reason, StatusChangedById=@ActorId, StatusChangedAt=SYSDATETIMEOFFSET()
                        OUTPUT INSERTED.BookingId, INSERTED.BookingNo, INSERTED.EmployeeId
                        FROM hr.Booking other
                        JOIN hr.Booking approved ON approved.BookingId=@Id
                        WHERE other.BookingId<>approved.BookingId AND other.VehicleId=approved.VehicleId
                          AND other.Status='Pending' AND other.ExpiresAt>SYSDATETIMEOFFSET()
                          AND other.StartAt<approved.EndAt AND other.EndAt>approved.StartAt
                        """, db, tx))
                    {
                        cancelOverlaps.Parameters.AddWithValue("@Reason", overlapReason);
                        cancelOverlaps.Parameters.AddWithValue("@ActorId", request.ActorId);
                        cancelOverlaps.Parameters.AddWithValue("@Id", bookingId);
                        await using var reader = await cancelOverlaps.ExecuteReaderAsync(ct);
                        while (await reader.ReadAsync(ct))
                            cancelledRequests.Add(new AffectedBooking(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));
                    }
                    foreach (var cancelled in cancelledRequests)
                        await AddBookingHistory(db, tx, cancelled.BookingId, "Cancelled", request.ActorId,
                            overlapReason, ct);

                }
                await tx.CommitAsync(ct);
                Dictionary<string, object?>? alternativeTime = null;
                if (request.Status == "Confirmed")
                {
                    try
                    {
                        alternativeVehicles = await ReadRows(db, """
                            SELECT v.VehicleId, v.PlateNo, v.Name AS VehicleName, v.Seats
                            FROM hr.Vehicle v
                            JOIN hr.Booking approved ON approved.BookingId=@Id
                            WHERE v.Status='Active' AND v.VehicleId<>approved.VehicleId
                              AND NOT EXISTS (SELECT 1 FROM hr.VehicleBlackout x WHERE x.VehicleId=v.VehicleId AND x.StartAt<approved.EndAt AND x.EndAt>approved.StartAt)
                              AND NOT EXISTS (SELECT 1 FROM hr.Booking b WHERE b.VehicleId=v.VehicleId
                                  AND (b.Status='Confirmed' OR (b.Status='Pending' AND b.ExpiresAt>SYSDATETIMEOFFSET()))
                                  AND b.StartAt<approved.EndAt AND b.EndAt>approved.StartAt)
                            ORDER BY v.PlateNo
                            """, ct, ("@Id", bookingId));
                        var times = await ReadRows(db, """
                            DECLARE @DurationMinutes int;
                            DECLARE @VehicleId int;
                            DECLARE @EndAt datetimeoffset;
                            SELECT @DurationMinutes=DATEDIFF(MINUTE,StartAt,EndAt), @VehicleId=VehicleId, @EndAt=EndAt
                            FROM hr.Booking WHERE BookingId=@Id;
                            ;WITH Slots AS (
                                SELECT @EndAt AS StartAt, 0 AS StepNo
                                UNION ALL
                                SELECT DATEADD(MINUTE,30,StartAt), StepNo+1 FROM Slots WHERE StepNo<336
                            )
                            SELECT TOP (1) s.StartAt, DATEADD(MINUTE,@DurationMinutes,s.StartAt) AS EndAt
                            FROM Slots s
                            WHERE NOT EXISTS (SELECT 1 FROM hr.VehicleBlackout x WHERE x.VehicleId=@VehicleId AND x.StartAt<DATEADD(MINUTE,@DurationMinutes,s.StartAt) AND x.EndAt>s.StartAt)
                              AND NOT EXISTS (SELECT 1 FROM hr.Booking b WHERE b.VehicleId=@VehicleId
                                  AND (b.Status='Confirmed' OR (b.Status='Pending' AND b.ExpiresAt>SYSDATETIMEOFFSET()))
                                  AND b.StartAt<DATEADD(MINUTE,@DurationMinutes,s.StartAt) AND b.EndAt>s.StartAt)
                            ORDER BY s.StartAt
                            OPTION (MAXRECURSION 0)
                            """, ct, ("@Id", bookingId));
                        alternativeTime = times.FirstOrDefault();
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Booking {BookingNo} was approved, but alternative availability suggestions could not be loaded.", bookingNo);
                    }
                }
                var notifications = cancelledRequests
                    .GroupBy(item => item.EmployeeId)
                    .Select(group => (group.Key, $"คำขอ {string.Join(", ", group.Select(item => item.BookingNo))} ถูกยกเลิก เนื่องจากมีคำขอเวลาเดียวกันได้รับอนุมัติแล้ว กรุณาเปิดหน้าเว็บเพื่อค้นหารถหรือช่วงเวลาอื่น"))
                    .ToList();
                if (request.Status is "Confirmed" or "Rejected" or "Cancelled")
                {
                    var statusMessage = request.Status switch
                    {
                        "Confirmed" => $"คำขอจองรถ {bookingNo} ได้รับอนุมัติแล้ว",
                        "Rejected" => $"คำขอจองรถ {bookingNo} ถูกปฏิเสธ กรุณาตรวจสอบเหตุผลในหน้าเว็บ",
                        _ => $"คำขอจองรถ {bookingNo} ถูกยกเลิก กรุณาตรวจสอบรายละเอียดในหน้าเว็บ"
                    };
                    notifications.Add((employeeId, statusMessage));
                }
                foreach (var notification in notifications)
                {
                    try { await NotifyLine(db, config, notification.Key, notification.Item2, ct); }
                    catch (Exception exception) { logger.LogWarning(exception, "LINE notification failed for booking {BookingNo} and employee {EmployeeId}.", bookingNo, notification.Key); }
                }
                return Results.Ok(new { bookingId, status = request.Status, cancelledRequests, alternativeVehicles, alternativeTime, message = "บันทึกการเปลี่ยนสถานะแล้ว" });
            }
            catch (Exception ex) { return Error(ex); }
        });

        api.MapPost("/vehicles", async (VehicleInput request, IConfiguration config, CancellationToken ct) =>
        {
            if (request.ActorId <= 0 || string.IsNullOrWhiteSpace(request.PlateNo) || request.PlateNo.Trim().Length > 20 || string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200 || request.Seats is < 1 or > 255)
                return Results.BadRequest(new { message = "กรุณาระบุผู้ดูแล ทะเบียน ชื่อรถ และจำนวนที่นั่งให้ถูกต้อง" });
            try
            {
                await using var db = await Open(config, ct);
                await using var tx = (SqlTransaction)await db.BeginTransactionAsync(ct);
                await using var cmd = new SqlCommand("INSERT INTO hr.Vehicle (PlateNo,Name,Seats,Status) OUTPUT INSERTED.VehicleId VALUES (@Plate,@Name,@Seats,'Active')", db, tx);
                cmd.Parameters.AddWithValue("@Plate", request.PlateNo.Trim()); cmd.Parameters.AddWithValue("@Name", request.Name.Trim()); cmd.Parameters.AddWithValue("@Seats", request.Seats);
                var id = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
                await using (var history = new SqlCommand("INSERT INTO hr.VehicleStatusHistory (VehicleId,Status,ActorEmployeeId,Reason,ChangedAt) VALUES (@Id,'Active',@Actor,N'เพิ่มรถ',SYSDATETIMEOFFSET())", db, tx))
                { history.Parameters.AddWithValue("@Id", id); history.Parameters.AddWithValue("@Actor", request.ActorId); await history.ExecuteNonQueryAsync(ct); }
                await tx.CommitAsync(ct);
                return Results.Created($"/api/booking/vehicles/{id}", new { vehicleId = id });
            }
            catch (Exception ex) { return Error(ex); }
        });

        api.MapPut("/vehicles/{vehicleId:int}", async (int vehicleId, VehicleInput request, IConfiguration config, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.PlateNo) || request.PlateNo.Trim().Length > 20 || string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200 || request.Seats is < 1 or > 255)
                return Results.BadRequest(new { message = "กรุณาระบุทะเบียน ชื่อรถ และจำนวนที่นั่งให้ถูกต้อง" });
            try
            {
                await using var db = await Open(config, ct);
                await using var cmd = new SqlCommand("UPDATE hr.Vehicle SET PlateNo=@Plate,Name=@Name,Seats=@Seats WHERE VehicleId=@Id AND Status='Active'", db);
                cmd.Parameters.AddWithValue("@Plate", request.PlateNo.Trim()); cmd.Parameters.AddWithValue("@Name", request.Name.Trim()); cmd.Parameters.AddWithValue("@Seats", request.Seats); cmd.Parameters.AddWithValue("@Id", vehicleId);
                if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound(new { message = "ไม่พบรถหรือรถถูกปิดใช้งานแล้ว" });
                return Results.Ok(new { vehicleId, message = "บันทึกข้อมูลรถแล้ว" });
            }
            catch (Exception ex) { return Error(ex); }
        });

        api.MapPatch("/vehicles/{vehicleId:int}/status", async (int vehicleId, VehicleStatusChange request, IConfiguration config, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (request.ActorId <= 0 || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 1000 ||
                request.Status == "Retired" && request.Reason.Trim().Length > 900 || request.Status is not ("Active" or "Retired"))
                return Results.BadRequest(new { message = "ระบุสถานะและเหตุผลการเปลี่ยนสถานะ" });
            try
            {
                await using var db = await Open(config, ct);
                await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                string? currentStatus;
                await using (var current = new SqlCommand("SELECT Status FROM hr.Vehicle WITH (UPDLOCK,HOLDLOCK) WHERE VehicleId=@Id", db, tx))
                {
                    current.Parameters.AddWithValue("@Id", vehicleId);
                    currentStatus = (string?)await current.ExecuteScalarAsync(ct);
                }
                if (currentStatus is null) { await tx.RollbackAsync(ct); return Results.NotFound(new { message = "ไม่พบรถ" }); }
                if (currentStatus == request.Status) { await tx.RollbackAsync(ct); return Results.Conflict(new { message = "รถอยู่ในสถานะนี้อยู่แล้ว" }); }
                var affected = request.Status == "Retired"
                    ? await FindAffectedBookings(db, tx, vehicleId, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, true, ct)
                    : [];
                var affectedError = ValidateAffectedBookings(affected, request.ConfirmAffectedBookings, request.AffectedBookingIds, "ปิดรถ");
                if (affectedError is not null) { await tx.RollbackAsync(ct); return affectedError; }
                if (request.Status == "Retired" && !await CancelAffectedBookings(db, tx, affected, request.ActorId, $"ปิดรถ: {request.Reason.Trim()}", ct))
                { await tx.RollbackAsync(ct); return Results.Conflict(new { message = "มีรายการจองเปลี่ยนแปลงระหว่างดำเนินการ กรุณาตรวจสอบและยืนยันใหม่" }); }
                await using (var update = new SqlCommand("UPDATE hr.Vehicle SET Status=@Status WHERE VehicleId=@Id", db, tx))
                { update.Parameters.AddWithValue("@Status", request.Status); update.Parameters.AddWithValue("@Id", vehicleId); await update.ExecuteNonQueryAsync(ct); }
                await using (var history = new SqlCommand("INSERT INTO hr.VehicleStatusHistory (VehicleId,Status,ActorEmployeeId,Reason,ChangedAt) VALUES (@Id,@Status,@Actor,@Reason,SYSDATETIMEOFFSET())", db, tx))
                {
                    history.Parameters.AddWithValue("@Id", vehicleId); history.Parameters.AddWithValue("@Status", request.Status); history.Parameters.AddWithValue("@Actor", request.ActorId); history.Parameters.AddWithValue("@Reason", request.Reason.Trim()); await history.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                if (request.Status == "Retired" && affected.Count > 0)
                    await NotifyAffectedBookings(db, config, affected, $"ปิดรถ {vehicleId}", loggerFactory.CreateLogger("BookingApi"), ct);
                return Results.Ok(new { vehicleId, status = request.Status, cancelledBookings = affected });
            }
            catch (Exception ex) { return Error(ex); }
        });

        api.MapPost("/blackouts", async (BlackoutInput request, IConfiguration config, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (request.ActorId <= 0 || request.VehicleId <= 0 || request.StartAt >= request.EndAt || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500)
                return Results.BadRequest(new { message = "ระบุรถ ช่วงเวลาซ่อม และเหตุผลให้ครบ โดยเวลาเริ่มต้องก่อนเวลาสิ้นสุด" });
            try
            {
                await using var db = await Open(config, ct);
                await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                await using (var vehicle = new SqlCommand("SELECT Status FROM hr.Vehicle WITH (UPDLOCK,HOLDLOCK) WHERE VehicleId=@Id", db, tx))
                {
                    vehicle.Parameters.AddWithValue("@Id", request.VehicleId);
                    if ((string?)await vehicle.ExecuteScalarAsync(ct) is null) { await tx.RollbackAsync(ct); return Results.NotFound(new { message = "ไม่พบรถ" }); }
                }
                var affected = await FindAffectedBookings(db, tx, request.VehicleId, request.StartAt, request.EndAt, false, ct);
                var affectedError = ValidateAffectedBookings(affected, request.ConfirmAffectedBookings, request.AffectedBookingIds, "กำหนดช่วงซ่อมบำรุง");
                if (affectedError is not null) { await tx.RollbackAsync(ct); return affectedError; }
                if (!await CancelAffectedBookings(db, tx, affected, request.ActorId, $"ปิดซ่อมบำรุง: {request.Reason.Trim()}", ct))
                { await tx.RollbackAsync(ct); return Results.Conflict(new { message = "มีรายการจองเปลี่ยนแปลงระหว่างดำเนินการ กรุณาตรวจสอบและยืนยันใหม่" }); }
                int id;
                await using (var insert = new SqlCommand("INSERT INTO hr.VehicleBlackout (VehicleId,StartAt,EndAt,Reason,CreatedById) OUTPUT INSERTED.VehicleBlackoutId VALUES (@Id,@Start,@End,@Reason,@Actor)", db, tx))
                {
                    insert.Parameters.AddWithValue("@Id", request.VehicleId); insert.Parameters.AddWithValue("@Start", request.StartAt); insert.Parameters.AddWithValue("@End", request.EndAt); insert.Parameters.AddWithValue("@Reason", request.Reason.Trim()); insert.Parameters.AddWithValue("@Actor", request.ActorId); id = Convert.ToInt32(await insert.ExecuteScalarAsync(ct));
                }
                await AddBlackoutHistory(db, tx, id, "Created", request.ActorId, null, null, null, null,
                    request.VehicleId, request.StartAt, request.EndAt, request.Reason.Trim(), ct);
                await tx.CommitAsync(ct);
                if (affected.Count > 0)
                    await NotifyAffectedBookings(db, config, affected, "กำหนดช่วงซ่อมบำรุง", loggerFactory.CreateLogger("BookingApi"), ct);
                return Results.Created($"/api/booking/blackouts/{id}", new { vehicleBlackoutId = id, cancelledBookings = affected });
            }
            catch (Exception ex) { return Error(ex); }
        });

        api.MapPut("/blackouts/{blackoutId:int}", async (int blackoutId, BlackoutInput request, IConfiguration config, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (request.ActorId <= 0 || request.VehicleId <= 0 || request.StartAt >= request.EndAt || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500)
                return Results.BadRequest(new { message = "ระบุรถ ช่วงเวลาซ่อม และเหตุผลให้ครบ โดยเวลาเริ่มต้องก่อนเวลาสิ้นสุด" });
            try
            {
                await using var db = await Open(config, ct);
                await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                int oldVehicleId;
                DateTimeOffset oldStartAt, oldEndAt;
                string oldReason;
                await using (var old = new SqlCommand("SELECT VehicleId,StartAt,EndAt,Reason FROM hr.VehicleBlackout WITH (UPDLOCK,HOLDLOCK) WHERE VehicleBlackoutId=@Id", db, tx))
                {
                    old.Parameters.AddWithValue("@Id", blackoutId);
                    await using var reader = await old.ExecuteReaderAsync(ct);
                    if (!await reader.ReadAsync(ct)) { await reader.DisposeAsync(); await tx.RollbackAsync(ct); return Results.NotFound(new { message = "ไม่พบช่วงซ่อมบำรุง" }); }
                    oldVehicleId = reader.GetInt32(0); oldStartAt = reader.GetDateTimeOffset(1); oldEndAt = reader.GetDateTimeOffset(2); oldReason = reader.GetString(3);
                }
                await using (var vehicle = new SqlCommand("SELECT Status FROM hr.Vehicle WITH (UPDLOCK,HOLDLOCK) WHERE VehicleId=@Id", db, tx))
                {
                    vehicle.Parameters.AddWithValue("@Id", request.VehicleId);
                    if ((string?)await vehicle.ExecuteScalarAsync(ct) is null) { await tx.RollbackAsync(ct); return Results.NotFound(new { message = "ไม่พบรถ" }); }
                }
                var affected = await FindAffectedBookings(db, tx, request.VehicleId, request.StartAt, request.EndAt, false, ct);
                var affectedError = ValidateAffectedBookings(affected, request.ConfirmAffectedBookings, request.AffectedBookingIds, "แก้ไขช่วงซ่อมบำรุง");
                if (affectedError is not null) { await tx.RollbackAsync(ct); return affectedError; }
                if (!await CancelAffectedBookings(db, tx, affected, request.ActorId, $"ปิดซ่อมบำรุง: {request.Reason.Trim()}", ct))
                { await tx.RollbackAsync(ct); return Results.Conflict(new { message = "มีรายการจองเปลี่ยนแปลงระหว่างดำเนินการ กรุณาตรวจสอบและยืนยันใหม่" }); }
                await using (var update = new SqlCommand("UPDATE hr.VehicleBlackout SET VehicleId=@VehicleId,StartAt=@Start,EndAt=@End,Reason=@Reason WHERE VehicleBlackoutId=@Id", db, tx))
                {
                    update.Parameters.AddWithValue("@VehicleId", request.VehicleId); update.Parameters.AddWithValue("@Start", request.StartAt); update.Parameters.AddWithValue("@End", request.EndAt); update.Parameters.AddWithValue("@Reason", request.Reason.Trim()); update.Parameters.AddWithValue("@Id", blackoutId);
                    if (await update.ExecuteNonQueryAsync(ct) == 0) { await tx.RollbackAsync(ct); return Results.NotFound(new { message = "ไม่พบช่วงซ่อมบำรุง" }); }
                }
                await AddBlackoutHistory(db, tx, blackoutId, "Updated", request.ActorId,
                    oldVehicleId, oldStartAt, oldEndAt, oldReason, request.VehicleId, request.StartAt, request.EndAt, request.Reason.Trim(), ct);
                await tx.CommitAsync(ct);
                if (affected.Count > 0)
                    await NotifyAffectedBookings(db, config, affected, "กำหนดช่วงซ่อมบำรุง", loggerFactory.CreateLogger("BookingApi"), ct);
                return Results.Ok(new { vehicleBlackoutId = blackoutId, cancelledBookings = affected, message = "แก้ไขช่วงซ่อมบำรุงแล้ว" });
            }
            catch (Exception ex) { return Error(ex); }
        });

        api.MapDelete("/blackouts/{blackoutId:int}", async (int blackoutId, int actorId, IConfiguration config, CancellationToken ct) =>
        {
            if (actorId <= 0) return Results.BadRequest(new { message = "ไม่พบข้อมูลผู้ดำเนินการ" });
            try
            {
                await using var db = await Open(config, ct);
                await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                int vehicleId; DateTimeOffset startAt, endAt; string reason;
                await using (var lookup = new SqlCommand("SELECT VehicleId,StartAt,EndAt,Reason FROM hr.VehicleBlackout WITH (UPDLOCK,HOLDLOCK) WHERE VehicleBlackoutId=@Id", db, tx))
                {
                    lookup.Parameters.AddWithValue("@Id", blackoutId);
                    await using var reader = await lookup.ExecuteReaderAsync(ct);
                    if (!await reader.ReadAsync(ct)) { await reader.DisposeAsync(); await tx.RollbackAsync(ct); return Results.NotFound(new { message = "ไม่พบช่วงซ่อมบำรุง" }); }
                    vehicleId = reader.GetInt32(0); startAt = reader.GetDateTimeOffset(1); endAt = reader.GetDateTimeOffset(2); reason = reader.GetString(3);
                }
                await using (var delete = new SqlCommand("DELETE FROM hr.VehicleBlackout WHERE VehicleBlackoutId=@Id", db, tx))
                { delete.Parameters.AddWithValue("@Id", blackoutId); await delete.ExecuteNonQueryAsync(ct); }
                await AddBlackoutHistory(db, tx, blackoutId, "Deleted", actorId,
                    vehicleId, startAt, endAt, reason, null, null, null, null, ct);
                await tx.CommitAsync(ct);
                return Results.Ok(new { message = "ลบช่วงซ่อมบำรุงแล้ว" });
            }
            catch (Exception ex) { return Error(ex); }
        });
    }

    private static async Task<List<ImpactedBooking>> FindAffectedBookings(
        SqlConnection db, SqlTransaction tx, int vehicleId, DateTimeOffset startAt, DateTimeOffset endAt,
        bool entireVehicle, CancellationToken ct)
    {
        var rangeFilter = entireVehicle ? "b.EndAt>SYSDATETIMEOFFSET()" : "b.EndAt>SYSDATETIMEOFFSET() AND b.StartAt<@EndAt AND b.EndAt>@StartAt";
        await using var cmd = new SqlCommand($"""
            SELECT b.BookingId, b.BookingNo, b.EmployeeId, e.DisplayName, v.PlateNo, v.Name, b.Status, b.StartAt, b.EndAt
            FROM hr.Booking b WITH (UPDLOCK,HOLDLOCK)
            JOIN hr.Employee e ON e.EmployeeId=b.EmployeeId
            JOIN hr.Vehicle v ON v.VehicleId=b.VehicleId
            WHERE b.VehicleId=@VehicleId
              AND (b.Status='Confirmed' OR (b.Status='Pending' AND b.ExpiresAt>SYSDATETIMEOFFSET()))
              AND {rangeFilter}
            ORDER BY b.StartAt, b.BookingId
            """, db, tx);
        cmd.Parameters.AddWithValue("@VehicleId", vehicleId);
        cmd.Parameters.AddWithValue("@StartAt", startAt);
        cmd.Parameters.AddWithValue("@EndAt", endAt);
        var rows = new List<ImpactedBooking>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new ImpactedBooking(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetDateTimeOffset(7), reader.GetDateTimeOffset(8)));
        return rows;
    }

    private static IResult? ValidateAffectedBookings(
        IReadOnlyList<ImpactedBooking> affected, bool confirmed, int[]? confirmedIds, string operation)
    {
        if (affected.Any(item => item.StartAt <= DateTimeOffset.UtcNow))
            return Results.Conflict(new { code = "BOOKING_ALREADY_STARTED", message = $"{operation} ไม่ได้ เพราะมีรายการที่เริ่มเดินทางแล้ว กรุณารอให้รายการนั้นสิ้นสุด", affectedBookings = affected });
        var actualIds = affected.Select(item => item.BookingId).OrderBy(id => id).ToArray();
        var suppliedIds = (confirmedIds ?? []).Distinct().OrderBy(id => id).ToArray();
        if (actualIds.Length > 0 && (!confirmed || !actualIds.SequenceEqual(suppliedIds)) ||
            confirmed && !actualIds.SequenceEqual(suppliedIds))
            return Results.Conflict(new { code = "AFFECTED_BOOKINGS_CONFIRMATION_REQUIRED", message = "มีรายการจองได้รับผลกระทบ กรุณาตรวจสอบรายการที่จะถูกยกเลิกและยืนยันอีกครั้ง", affectedBookings = affected });
        return null;
    }

    private static async Task<bool> CancelAffectedBookings(
        SqlConnection db, SqlTransaction tx, IReadOnlyList<ImpactedBooking> affected,
        int actorId, string reason, CancellationToken ct)
    {
        foreach (var item in affected)
        {
            await using var cmd = new SqlCommand("""
                UPDATE hr.Booking
                SET Status='Cancelled', StatusReason=@Reason, StatusChangedById=@ActorId, StatusChangedAt=SYSDATETIMEOFFSET()
                WHERE BookingId=@BookingId AND StartAt>SYSDATETIMEOFFSET()
                  AND (Status='Confirmed' OR (Status='Pending' AND ExpiresAt>SYSDATETIMEOFFSET()))
                """, db, tx);
            var itemReason = $"ยกเลิกเนื่องจาก{reason}";
            cmd.Parameters.AddWithValue("@Reason", itemReason);
            cmd.Parameters.AddWithValue("@ActorId", actorId);
            cmd.Parameters.AddWithValue("@BookingId", item.BookingId);
            if (await cmd.ExecuteNonQueryAsync(ct) != 1) return false;
            await AddBookingHistory(db, tx, item.BookingId, "Cancelled", actorId, itemReason, ct);
        }
        return true;
    }

    private static async Task NotifyAffectedBookings(
        SqlConnection db, IConfiguration config, IReadOnlyList<ImpactedBooking> affected,
        string operation, ILogger? logger, CancellationToken ct)
    {
        foreach (var group in affected.GroupBy(item => item.EmployeeId))
        {
            var message = $"รายการจอง {string.Join(", ", group.Select(item => item.BookingNo))} ถูกยกเลิกเนื่องจาก{operation} กรุณาเปิดหน้าเว็บเพื่อตรวจสอบและเลือกเวลาหรือรถคันอื่น";
            try { await NotifyLine(db, config, group.Key, message, ct); }
            catch (Exception exception) { logger?.LogWarning(exception, "LINE notification after {Operation} failed for employee {EmployeeId}.", operation, group.Key); }
        }
    }

    private static async Task AddBlackoutHistory(
        SqlConnection db, SqlTransaction tx, int blackoutId, string action, int actorId,
        int? oldVehicleId, DateTimeOffset? oldStartAt, DateTimeOffset? oldEndAt, string? oldReason,
        int? newVehicleId, DateTimeOffset? newStartAt, DateTimeOffset? newEndAt, string? newReason,
        CancellationToken ct)
    {
        await using var cmd = new SqlCommand("""
            INSERT INTO hr.VehicleBlackoutHistory
                (VehicleBlackoutId,Action,ActorEmployeeId,ChangedAt,OldVehicleId,OldStartAt,OldEndAt,OldReason,NewVehicleId,NewStartAt,NewEndAt,NewReason)
            VALUES (@Id,@Action,@Actor,SYSDATETIMEOFFSET(),@OldVehicleId,@OldStartAt,@OldEndAt,@OldReason,@NewVehicleId,@NewStartAt,@NewEndAt,@NewReason)
            """, db, tx);
        cmd.Parameters.AddWithValue("@Id", blackoutId);
        cmd.Parameters.AddWithValue("@Action", action);
        cmd.Parameters.AddWithValue("@Actor", actorId);
        cmd.Parameters.AddWithValue("@OldVehicleId", (object?)oldVehicleId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@OldStartAt", (object?)oldStartAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@OldEndAt", (object?)oldEndAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@OldReason", (object?)oldReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@NewVehicleId", (object?)newVehicleId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@NewStartAt", (object?)newStartAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@NewEndAt", (object?)newEndAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@NewReason", (object?)newReason ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task ExpirePendingBookings(IConfiguration config, CancellationToken ct, ILogger? logger = null)
    {
        await using var db = await Open(config, ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var expired = new List<(int BookingId, int EmployeeId)>();
        await using (var update = new SqlCommand("""
            UPDATE hr.Booking
            SET Status='Cancelled', StatusReason=N'คำขอหมดอายุก่อนเวลาออกเดินทางหรือครบกำหนด 3 ชั่วโมง',
                StatusChangedById=EmployeeId, StatusChangedAt=SYSDATETIMEOFFSET()
            OUTPUT INSERTED.BookingId, INSERTED.EmployeeId
            WHERE Status='Pending' AND (ExpiresAt<=SYSDATETIMEOFFSET() OR StartAt<=SYSDATETIMEOFFSET())
            """, db, tx))
        await using (var reader = await update.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) expired.Add((reader.GetInt32(0), reader.GetInt32(1)));
        }
        foreach (var item in expired)
            await AddBookingHistory(db, tx, item.BookingId, "Cancelled", item.EmployeeId, "คำขอหมดอายุก่อนเวลาออกเดินทางหรือครบกำหนด 3 ชั่วโมง", ct);
        await tx.CommitAsync(ct);
        foreach (var item in expired)
        {
            try { await NotifyLine(db, config, item.EmployeeId, $"คำขอจองรถ #{item.BookingId} หมดอายุและถูกยกเลิกก่อนเวลาออกเดินทาง", ct); }
            catch (Exception exception) { logger?.LogWarning(exception, "LINE expiry notification failed for booking {BookingId}.", item.BookingId); }
        }
    }

    private static async Task<SqlConnection> Open(IConfiguration config, CancellationToken ct)
    {
        var text = config.GetConnectionString("MosciiHR");
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("ยังไม่ได้ตั้งค่า ConnectionStrings:MosciiHR");
        var builder = new SqlConnectionStringBuilder(text);
        if (string.IsNullOrEmpty(builder.Password)) builder.Password = config["Database:Password"] ?? "";
        if (!builder.IntegratedSecurity && string.IsNullOrEmpty(builder.Password))
            throw new InvalidOperationException("ยังไม่ได้ตั้งรหัสผ่าน SQL Server กรุณากำหนด Database__Password ใน environment ก่อนเริ่มระบบ");
        var db = new SqlConnection(builder.ConnectionString);
        await db.OpenAsync(ct);
        return db;
    }

    private static async Task<List<Dictionary<string, object?>>> ReadRows(SqlConnection db, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var cmd = new SqlCommand(sql, db);
        foreach (var arg in args)
        {
            if (arg.Name == "@ActorId")
            {
                var parameter = cmd.Parameters.Add(arg.Name, SqlDbType.Int);
                parameter.Value = arg.Value ?? DBNull.Value;
            }
            else cmd.Parameters.AddWithValue(arg.Name, arg.Value ?? DBNull.Value);
        }
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
            result.Add(row);
        }
        return result;
    }

    private static async Task NotifyLine(SqlConnection db, IConfiguration config, int employeeId, string message, CancellationToken ct)
    {
        var token = config["Line:ChannelAccessToken"];
        if (string.IsNullOrWhiteSpace(token)) return;
        var lineUserIds = new List<string>();
        await using (var lookup = new SqlCommand("SELECT LineUserId FROM hr.LineAccount WHERE EmployeeId=@EmployeeId AND UnlinkedAt IS NULL", db))
        {
            lookup.Parameters.AddWithValue("@EmployeeId", employeeId);
            await using var reader = await lookup.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) lineUserIds.Add(reader.GetString(0));
        }
        foreach (var lineUserId in lineUserIds)
        {
            if (lineUserId.StartsWith("DEMO-", StringComparison.OrdinalIgnoreCase)) continue;
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.line.me/v2/bot/message/push");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-Line-Retry-Key", Guid.NewGuid().ToString());
            request.Content = JsonContent.Create(new
            {
                to = lineUserId,
                messages = new[] { new { type = "text", text = message } }
            });
            using var response = await LineClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        }
    }

    private static async Task AddBookingHistory(SqlConnection db, SqlTransaction tx, int bookingId, string status, int actorId, string reason, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("INSERT INTO hr.BookingStatusHistory (BookingId,Status,ActorEmployeeId,Reason,ChangedAt) VALUES (@Id,@Status,@Actor,@Reason,SYSDATETIMEOFFSET())", db, tx);
        cmd.Parameters.AddWithValue("@Id", bookingId); cmd.Parameters.AddWithValue("@Status", status); cmd.Parameters.AddWithValue("@Actor", actorId); cmd.Parameters.AddWithValue("@Reason", reason);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static IResult Error(Exception ex) => ex switch
    {
        SqlException sql when sql.Number is 2601 or 2627 => Results.Conflict(new { message = "ข้อมูลซ้ำ กรุณาตรวจทะเบียนรถหรือคีย์คำขอ" }),
        SqlException sql when sql.Number == 547 => Results.Conflict(new { message = "ข้อมูลอ้างอิงไม่ถูกต้อง หรือมีรายการเกี่ยวข้องที่ยังใช้งานอยู่" }),
        SqlException sql when sql.Number == 208 => Results.Json(new { message = $"ไม่พบตารางที่ API ต้องใช้ใน schema hr: {sql.Message} กรุณารัน database/dev/vehicle-booking.sql หลังจากติดตั้ง hr.Employee แล้ว" }, statusCode: 503),
        SqlException sql when sql.Number == 207 => Results.Json(new { message = $"คอลัมน์ในตารางจองรถไม่ตรงกับ API: {sql.Message} ตรวจ schema แล้วรัน migration จองรถให้ครบ" }, statusCode: 503),
        SqlException sql when sql.Number == 229 => Results.Json(new { message = $"บัญชีฐานข้อมูลไม่มีสิทธิ์อ่านหรือเขียนตารางจองรถ: {sql.Message}" }, statusCode: 503),
        SqlException sql when sql.Number is 18456 or 4060 => Results.Json(new { message = $"เชื่อมต่อฐานข้อมูล HRMOSCII ไม่สำเร็จ: {sql.Message}" }, statusCode: 503),
        SqlException sql => Results.Json(new { message = $"SQL Server error {sql.Number}: {sql.Message}" }, statusCode: 503),
        InvalidOperationException => Results.Json(new { message = ex.Message }, statusCode: 503),
        _ => Results.Json(new { message = "เกิดข้อผิดพลาดในการบันทึกข้อมูล กรุณาลองใหม่" }, statusCode: 500)
    };

    public sealed record CreateBooking(int EmployeeId, int VehicleId, DateTimeOffset StartAt, DateTimeOffset EndAt, string Destination, string Purpose, string RequestKey);
    private sealed record AffectedBooking(int BookingId, string BookingNo, int EmployeeId);
    public sealed record ImpactedBooking(int BookingId, string BookingNo, int EmployeeId, string EmployeeName, string PlateNo, string VehicleName, string Status, DateTimeOffset StartAt, DateTimeOffset EndAt);
    public sealed record ChangeStatus(int ActorId, bool IsAdmin, string Status, string? Reason);
    public sealed record VehicleInput(int ActorId, string PlateNo, string Name, int Seats);
    public sealed record VehicleStatusChange(int ActorId, string Status, string Reason, bool ConfirmAffectedBookings = false, int[]? AffectedBookingIds = null);
    public sealed record BlackoutInput(int ActorId, int VehicleId, DateTimeOffset StartAt, DateTimeOffset EndAt, string Reason, bool ConfirmAffectedBookings = false, int[]? AffectedBookingIds = null);
}
