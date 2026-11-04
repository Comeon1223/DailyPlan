#include "dailyplan_core.h"

#include "nlohmann/json.hpp"
#include "sqlite3.h"

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <mutex>
#include <new>
#include <string>
#include <utility>
#include <vector>

// The header exposes this type opaquely; defining it in the global namespace
// keeps the C ABI signature identical while allowing internal access.
struct DpContext {
    sqlite3* db = nullptr;
    std::mutex mutex;
};

namespace {

using json = nlohmann::json;

constexpr const char* kVersion = "1.1.0";

constexpr const char* kTaskColumns =
    "id,title,notes,start_time,end_time,status,start_notified,end_notified,"
    "created_at,updated_at,repeat_daily";

constexpr const char* kSchema = R"sql(
CREATE TABLE IF NOT EXISTS tasks (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    title           TEXT    NOT NULL,
    notes           TEXT    NOT NULL DEFAULT '',
    start_time      TEXT    NOT NULL,
    end_time        TEXT    NOT NULL,
    status          TEXT    NOT NULL DEFAULT 'pending'
                            CHECK (status IN ('pending','in_progress','completed','cancelled')),
    start_notified  INTEGER NOT NULL DEFAULT 0,
    end_notified    INTEGER NOT NULL DEFAULT 0,
    created_at      TEXT    NOT NULL,
    updated_at      TEXT    NOT NULL,
    repeat_daily    INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_tasks_start_time ON tasks(start_time);
CREATE INDEX IF NOT EXISTS idx_tasks_due ON tasks(status, start_notified, end_notified);
CREATE TABLE IF NOT EXISTS task_day_status (
    task_id     INTEGER NOT NULL,
    day_date    TEXT    NOT NULL,
    status      TEXT    NOT NULL
                        CHECK (status IN ('pending','in_progress','completed','cancelled')),
    updated_at  TEXT    NOT NULL,
    PRIMARY KEY (task_id, day_date),
    FOREIGN KEY (task_id) REFERENCES tasks(id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS reminder_events (
    task_id         INTEGER NOT NULL,
    occurrence_date TEXT    NOT NULL,
    event           TEXT    NOT NULL CHECK (event IN ('start','end')),
    notified_at     TEXT    NOT NULL,
    PRIMARY KEY (task_id, occurrence_date, event),
    FOREIGN KEY (task_id) REFERENCES tasks(id) ON DELETE CASCADE
);
)sql";

struct TaskInput {
    std::string title;
    std::string notes;
    std::string start_time;
    std::string end_time;
    std::string status;
    bool repeat_daily = false;
};

struct Reminder {
    long long task_id = 0;
    std::string title;
    std::string event;
    std::string due_at;
    std::string status;
};

class Statement {
public:
    Statement() = default;
    ~Statement() {
        if (stmt_ != nullptr) {
            sqlite3_finalize(stmt_);
        }
    }

    Statement(const Statement&) = delete;
    Statement& operator=(const Statement&) = delete;

    bool prepare(sqlite3* db, const std::string& sql, std::string& error) {
        const int rc = sqlite3_prepare_v2(db, sql.c_str(), -1, &stmt_, nullptr);
        if (rc != SQLITE_OK) {
            error = sqlite3_errmsg(db);
            return false;
        }
        return true;
    }

    sqlite3_stmt* get() const { return stmt_; }

private:
    sqlite3_stmt* stmt_ = nullptr;
};

class Transaction {
public:
    explicit Transaction(sqlite3* db) : db_(db) {}
    ~Transaction() {
        if (active_) {
            sqlite3_exec(db_, "ROLLBACK;", nullptr, nullptr, nullptr);
        }
    }

    bool begin(std::string& error) {
        if (!exec("BEGIN IMMEDIATE;", error)) {
            return false;
        }
        active_ = true;
        return true;
    }

    bool commit(std::string& error) {
        if (!exec("COMMIT;", error)) {
            return false;
        }
        active_ = false;
        return true;
    }

    Transaction(const Transaction&) = delete;
    Transaction& operator=(const Transaction&) = delete;

private:
    bool exec(const char* sql, std::string& error) {
        char* message = nullptr;
        const int rc = sqlite3_exec(db_, sql, nullptr, nullptr, &message);
        if (rc != SQLITE_OK) {
            error = message != nullptr ? message : sqlite3_errmsg(db_);
            sqlite3_free(message);
            return false;
        }
        return true;
    }

    sqlite3* db_;
    bool active_ = false;
};

void clear_error(char** error_message) {
    if (error_message != nullptr) {
        *error_message = nullptr;
    }
}

void set_error(char** error_message, const std::string& message) {
    if (error_message == nullptr) {
        return;
    }
    const std::size_t size = message.size();
    auto* copy = static_cast<char*>(std::malloc(size + 1));
    if (copy == nullptr) {
        *error_message = nullptr;
        return;
    }
    std::memcpy(copy, message.data(), size);
    copy[size] = '\0';
    *error_message = copy;
}

char* to_c_string(const std::string& value) {
    const std::size_t size = value.size();
    auto* copy = static_cast<char*>(std::malloc(size + 1));
    if (copy == nullptr) {
        return nullptr;
    }
    std::memcpy(copy, value.data(), size);
    copy[size] = '\0';
    return copy;
}

void set_error_from_sqlite(char** error_message, sqlite3* db) {
    set_error(error_message, db != nullptr ? sqlite3_errmsg(db) : "SQLite 操作失败");
}

bool exec_sql(sqlite3* db, const char* sql, std::string& error) {
    char* message = nullptr;
    const int rc = sqlite3_exec(db, sql, nullptr, nullptr, &message);
    if (rc != SQLITE_OK) {
        error = message != nullptr ? message : sqlite3_errmsg(db);
        sqlite3_free(message);
        return false;
    }
    return true;
}

std::string trim_ascii(const std::string& text) {
    const auto first = text.find_first_not_of(" \t\r\n");
    if (first == std::string::npos) {
        return {};
    }
    const auto last = text.find_last_not_of(" \t\r\n");
    return text.substr(first, last - first + 1);
}

bool is_ascii_digit(char c) {
    return c >= '0' && c <= '9';
}

bool read_number(const std::string& text, std::size_t pos, std::size_t length, int& value) {
    int result = 0;
    for (std::size_t i = 0; i < length; ++i) {
        const char c = text[pos + i];
        if (!is_ascii_digit(c)) {
            return false;
        }
        result = result * 10 + (c - '0');
    }
    value = result;
    return true;
}

bool is_leap_year(int year) {
    return (year % 4 == 0 && year % 100 != 0) || (year % 400 == 0);
}

int days_in_month(int year, int month) {
    static constexpr int days[] = {31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31};
    if (month == 2) {
        return is_leap_year(year) ? 29 : 28;
    }
    return days[month - 1];
}

struct DateTimeParts {
    int year = 0;
    int month = 0;
    int day = 0;
    int hour = 0;
    int minute = 0;
    int second = 0;
};

// Strict local-time format: yyyy-MM-ddTHH:mm:ss.
bool parse_datetime_parts(const std::string& value, DateTimeParts& parts) {
    if (value.size() != 19) {
        return false;
    }
    if (value[4] != '-' || value[7] != '-' || value[10] != 'T' ||
        value[13] != ':' || value[16] != ':') {
        return false;
    }

    int year = 0;
    int month = 0;
    int day = 0;
    int hour = 0;
    int minute = 0;
    int second = 0;
    if (!read_number(value, 0, 4, year) ||
        !read_number(value, 5, 2, month) ||
        !read_number(value, 8, 2, day) ||
        !read_number(value, 11, 2, hour) ||
        !read_number(value, 14, 2, minute) ||
        !read_number(value, 17, 2, second)) {
        return false;
    }
    if (year < 1900 || year > 9999) {
        return false;
    }
    if (month < 1 || month > 12) {
        return false;
    }
    if (day < 1 || day > days_in_month(year, month)) {
        return false;
    }
    if (hour < 0 || hour > 23 || minute < 0 || minute > 59 ||
        second < 0 || second > 59) {
        return false;
    }

    parts = {year, month, day, hour, minute, second};
    return true;
}

bool is_valid_datetime(const std::string& value) {
    DateTimeParts parts;
    return parse_datetime_parts(value, parts);
}

bool is_valid_date(const std::string& value) {
    return value.size() == 10 && is_valid_datetime(value + "T00:00:00");
}

// Calendar arithmetic without time zones. The core only ever compares local
// clock values, so treating them as timezone-free wall-clock seconds is exact.
int64_t days_from_civil(int year, unsigned month, unsigned day) {
    year -= month <= 2;
    const int era = (year >= 0 ? year : year - 399) / 400;
    const unsigned year_of_era = static_cast<unsigned>(year - era * 400);
    const unsigned day_of_year =
        (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1;
    const unsigned day_of_era =
        year_of_era * 365 + year_of_era / 4 - year_of_era / 100 + day_of_year;
    return static_cast<int64_t>(era) * 146097 + static_cast<int64_t>(day_of_era) -
           719468;
}

void civil_from_days(int64_t days, int& year, unsigned& month, unsigned& day) {
    days += 719468;
    const int64_t era = (days >= 0 ? days : days - 146096) / 146097;
    const unsigned day_of_era = static_cast<unsigned>(days - era * 146097);
    const unsigned year_of_era =
        (day_of_era - day_of_era / 1460 + day_of_era / 36524 -
         day_of_era / 146096) / 365;
    year = static_cast<int>(year_of_era) + static_cast<int>(era) * 400;
    const unsigned day_of_year =
        day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    const unsigned month_prime = (5 * day_of_year + 2) / 153;
    day = day_of_year - (153 * month_prime + 2) / 5 + 1;
    month = month_prime + (month_prime < 10 ? 3 : -9);
    year += month <= 2;
}

int64_t datetime_to_seconds(const DateTimeParts& parts) {
    return days_from_civil(parts.year, static_cast<unsigned>(parts.month),
                           static_cast<unsigned>(parts.day)) *
               86400 +
           static_cast<int64_t>(parts.hour) * 3600 +
           static_cast<int64_t>(parts.minute) * 60 + parts.second;
}

bool datetime_to_seconds(const std::string& value, int64_t& seconds) {
    DateTimeParts parts;
    if (!parse_datetime_parts(value, parts)) {
        return false;
    }
    seconds = datetime_to_seconds(parts);
    return true;
}

std::string seconds_to_datetime(int64_t total_seconds) {
    int64_t days = total_seconds / 86400;
    int64_t remainder = total_seconds % 86400;
    if (remainder < 0) {
        remainder += 86400;
        --days;
    }

    int year = 0;
    unsigned month = 0;
    unsigned day = 0;
    civil_from_days(days, year, month, day);
    const int hour = static_cast<int>(remainder / 3600);
    const int minute = static_cast<int>((remainder % 3600) / 60);
    const int second = static_cast<int>(remainder % 60);

    char buffer[32];
    std::snprintf(buffer, sizeof(buffer), "%04d-%02u-%02uT%02d:%02d:%02d",
                  year, month, day, hour, minute, second);
    return buffer;
}

std::string add_days_to_date(const std::string& date, int days) {
    int64_t seconds = 0;
    if (!datetime_to_seconds(date + "T00:00:00", seconds)) {
        return {};
    }
    return seconds_to_datetime(seconds + static_cast<int64_t>(days) * 86400)
        .substr(0, 10);
}

bool make_occurrence(const std::string& day, const std::string& base_start,
                     const std::string& base_end, std::string& start,
                     std::string& end) {
    int64_t base_start_seconds = 0;
    int64_t base_end_seconds = 0;
    if (!datetime_to_seconds(base_start, base_start_seconds) ||
        !datetime_to_seconds(base_end, base_end_seconds)) {
        return false;
    }

    int64_t occurrence_start_seconds = 0;
    if (!datetime_to_seconds(day + "T" + base_start.substr(11, 8),
                             occurrence_start_seconds)) {
        return false;
    }

    start = seconds_to_datetime(occurrence_start_seconds);
    end = seconds_to_datetime(occurrence_start_seconds +
                              (base_end_seconds - base_start_seconds));
    return true;
}

bool is_valid_status(const std::string& status) {
    return status == "pending" || status == "in_progress" ||
           status == "completed" || status == "cancelled";
}

std::string status_check_list() {
    return "状态必须是 pending、in_progress、completed 或 cancelled";
}

bool parse_task_input(const char* task_json, TaskInput& output, std::string& error) {
    if (task_json == nullptr) {
        error = "任务数据不能为空";
        return false;
    }

    json value = json::parse(task_json, nullptr, false);
    if (value.is_discarded() || !value.is_object()) {
        error = "任务数据不是有效的 JSON 对象";
        return false;
    }

    if (!value.contains("title") || !value["title"].is_string()) {
        error = "任务标题不能为空";
        return false;
    }
    if (value.contains("notes") && !value["notes"].is_string()) {
        error = "备注格式无效";
        return false;
    }
    if (!value.contains("start_time") || !value["start_time"].is_string() ||
        !value.contains("end_time") || !value["end_time"].is_string()) {
        error = "开始时间和结束时间不能为空";
        return false;
    }

    if (value.contains("repeat_daily") && !value["repeat_daily"].is_boolean()) {
        error = "repeat_daily 必须是布尔值";
        return false;
    }

    output.title = trim_ascii(value["title"].get<std::string>());
    output.notes = value.value("notes", std::string{});
    output.start_time = value["start_time"].get<std::string>();
    output.end_time = value["end_time"].get<std::string>();
    output.status = value.value("status", std::string{"pending"});
    output.repeat_daily = value.value("repeat_daily", false);

    if (output.title.empty()) {
        error = "任务标题不能为空";
        return false;
    }
    if (output.title.size() > 500) {
        error = "任务标题过长";
        return false;
    }
    if (output.notes.size() > 10000) {
        error = "备注不能超过 10000 字节";
        return false;
    }
    if (!is_valid_datetime(output.start_time)) {
        error = "开始时间格式无效，应为 yyyy-MM-ddTHH:mm:ss";
        return false;
    }
    if (!is_valid_datetime(output.end_time)) {
        error = "结束时间格式无效，应为 yyyy-MM-ddTHH:mm:ss";
        return false;
    }
    if (output.end_time <= output.start_time) {
        error = "结束时间必须晚于开始时间";
        return false;
    }
    if (!is_valid_status(output.status)) {
        error = status_check_list();
        return false;
    }
    if (output.repeat_daily) {
        int64_t start_seconds = 0;
        int64_t end_seconds = 0;
        datetime_to_seconds(output.start_time, start_seconds);
        datetime_to_seconds(output.end_time, end_seconds);
        if (end_seconds - start_seconds > 86400) {
            error = "每天重复的任务时长不能超过 24 小时";
            return false;
        }
        // The series itself has no global completion state; each day owns its
        // own status in task_day_status.
        output.status = "pending";
    }
    return true;
}

std::string column_text(sqlite3_stmt* stmt, int index) {
    const auto* text = sqlite3_column_text(stmt, index);
    if (text == nullptr) {
        return {};
    }
    return reinterpret_cast<const char*>(text);
}

json task_from_row(sqlite3_stmt* stmt) {
    json task;
    task["id"] = sqlite3_column_int64(stmt, 0);
    task["title"] = column_text(stmt, 1);
    task["notes"] = column_text(stmt, 2);
    task["start_time"] = column_text(stmt, 3);
    task["end_time"] = column_text(stmt, 4);
    task["status"] = column_text(stmt, 5);
    task["start_notified"] = sqlite3_column_int(stmt, 6) != 0;
    task["end_notified"] = sqlite3_column_int(stmt, 7) != 0;
    task["created_at"] = column_text(stmt, 8);
    task["updated_at"] = column_text(stmt, 9);
    task["repeat_daily"] = sqlite3_column_int(stmt, 10) != 0;
    return task;
}

bool table_has_column(sqlite3* db, const char* table, const char* column,
                      bool& found, std::string& error) {
    const std::string sql = std::string("PRAGMA table_info(") + table + ");";
    Statement statement;
    if (!statement.prepare(db, sql, error)) {
        return false;
    }

    found = false;
    int rc = SQLITE_ROW;
    while ((rc = sqlite3_step(statement.get())) == SQLITE_ROW) {
        if (column_text(statement.get(), 1) == column) {
            found = true;
            return true;
        }
    }
    if (rc != SQLITE_DONE) {
        error = sqlite3_errmsg(db);
        return false;
    }
    return true;
}

bool ensure_schema(sqlite3* db, std::string& error) {
    bool found = false;
    if (!table_has_column(db, "tasks", "repeat_daily", found, error)) {
        return false;
    }
    if (!found &&
        !exec_sql(db, "ALTER TABLE tasks ADD COLUMN "
                       "repeat_daily INTEGER NOT NULL DEFAULT 0;",
                  error)) {
        return false;
    }
    return true;
}

bool load_task_locked(sqlite3* db, long long id, json& task, bool& found, std::string& error) {
    const std::string sql =
        std::string("SELECT ") + kTaskColumns + " FROM tasks WHERE id = ?;";
    Statement statement;
    if (!statement.prepare(db, sql, error)) {
        return false;
    }
    sqlite3_bind_int64(statement.get(), 1, id);
    const int rc = sqlite3_step(statement.get());
    if (rc == SQLITE_ROW) {
        task = task_from_row(statement.get());
        found = true;
        return true;
    }
    if (rc != SQLITE_DONE) {
        error = sqlite3_errmsg(db);
        return false;
    }
    found = false;
    return true;
}

bool insert_task_locked(DpContext* ctx, const TaskInput& input, json& task,
                        std::string& error) {
    const char* sql = R"sql(
INSERT INTO tasks
    (title, notes, start_time, end_time, status, start_notified, end_notified,
     created_at, updated_at, repeat_daily)
VALUES
    (?, ?, ?, ?, ?, 0, 0,
     strftime('%Y-%m-%dT%H:%M:%S', 'now', 'localtime'),
     strftime('%Y-%m-%dT%H:%M:%S', 'now', 'localtime'), ?);
)sql";

    Statement statement;
    if (!statement.prepare(ctx->db, sql, error)) {
        return false;
    }
    sqlite3_bind_text(statement.get(), 1, input.title.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(statement.get(), 2, input.notes.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(statement.get(), 3, input.start_time.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(statement.get(), 4, input.end_time.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(statement.get(), 5, input.status.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_int(statement.get(), 6, input.repeat_daily ? 1 : 0);

    const int rc = sqlite3_step(statement.get());
    if (rc != SQLITE_DONE) {
        error = sqlite3_errmsg(ctx->db);
        return false;
    }
    const long long id = sqlite3_last_insert_rowid(ctx->db);

    bool found = false;
    if (!load_task_locked(ctx->db, id, task, found, error)) {
        return false;
    }
    if (!found) {
        error = "创建任务后无法读取该任务";
        return false;
    }
    return true;
}

bool update_task_locked(DpContext* ctx, long long id, const TaskInput& input,
                        json& task, std::string& error) {
    Transaction transaction(ctx->db);
    if (!transaction.begin(error)) {
        return false;
    }

    json old_task;
    bool found = false;
    if (!load_task_locked(ctx->db, id, old_task, found, error)) {
        return false;
    }
    if (!found) {
        error = "任务不存在";
        return false;
    }

    const bool old_repeat = old_task["repeat_daily"].get<bool>();
    const bool repeat_changed = old_repeat != input.repeat_daily;
    const bool schedule_changed =
        repeat_changed ||
        old_task["start_time"].get<std::string>() != input.start_time ||
        old_task["end_time"].get<std::string>() != input.end_time;

    const char* sql = R"sql(
UPDATE tasks SET
    title = ?,
    notes = ?,
    start_time = ?,
    end_time = ?,
    status = ?,
    repeat_daily = ?,
    start_notified = CASE
        WHEN start_time <> ? OR repeat_daily <> ? THEN 0
        ELSE start_notified END,
    end_notified = CASE
        WHEN end_time <> ? OR repeat_daily <> ? THEN 0
        ELSE end_notified END,
    updated_at = strftime('%Y-%m-%dT%H:%M:%S', 'now', 'localtime')
WHERE id = ?;
)sql";

    Statement statement;
    if (!statement.prepare(ctx->db, sql, error)) {
        return false;
    }
    sqlite3_bind_text(statement.get(), 1, input.title.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(statement.get(), 2, input.notes.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(statement.get(), 3, input.start_time.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(statement.get(), 4, input.end_time.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(statement.get(), 5, input.status.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_int(statement.get(), 6, input.repeat_daily ? 1 : 0);
    sqlite3_bind_text(statement.get(), 7, input.start_time.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_int(statement.get(), 8, input.repeat_daily ? 1 : 0);
    sqlite3_bind_text(statement.get(), 9, input.end_time.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_int(statement.get(), 10, input.repeat_daily ? 1 : 0);
    sqlite3_bind_int64(statement.get(), 11, id);

    const int rc = sqlite3_step(statement.get());
    if (rc != SQLITE_DONE) {
        error = sqlite3_errmsg(ctx->db);
        return false;
    }

    if (schedule_changed) {
        Statement clear_events;
        if (!clear_events.prepare(
                ctx->db, "DELETE FROM reminder_events WHERE task_id = ?;", error)) {
            return false;
        }
        sqlite3_bind_int64(clear_events.get(), 1, id);
        if (sqlite3_step(clear_events.get()) != SQLITE_DONE) {
            error = sqlite3_errmsg(ctx->db);
            return false;
        }
    }

    if (repeat_changed) {
        Statement clear_days;
        if (!clear_days.prepare(
                ctx->db, "DELETE FROM task_day_status WHERE task_id = ?;", error)) {
            return false;
        }
        sqlite3_bind_int64(clear_days.get(), 1, id);
        if (sqlite3_step(clear_days.get()) != SQLITE_DONE) {
            error = sqlite3_errmsg(ctx->db);
            return false;
        }
    }

    if (!load_task_locked(ctx->db, id, task, found, error)) {
        return false;
    }
    if (!found) {
        error = "任务不存在";
        return false;
    }

    return transaction.commit(error);
}

bool collect_due(sqlite3* db, const char* time_column, const char* notified_column,
                 const std::string& now_iso, std::vector<Reminder>& reminders,
                 std::string& error) {
    const std::string select_sql =
        std::string("SELECT id,title,") + time_column + ",status FROM tasks "
        "WHERE " + notified_column + " = 0 "
        "AND repeat_daily = 0 "
        "AND status IN ('pending','in_progress') "
        "AND " + time_column + " <= ? "
        "ORDER BY " + time_column + " ASC, id ASC LIMIT 100;";

    Statement select_statement;
    if (!select_statement.prepare(db, select_sql, error)) {
        return false;
    }
    sqlite3_bind_text(select_statement.get(), 1, now_iso.c_str(), -1, SQLITE_TRANSIENT);

    std::vector<Reminder> found;
    int rc = SQLITE_ROW;
    while ((rc = sqlite3_step(select_statement.get())) == SQLITE_ROW) {
        Reminder reminder;
        reminder.task_id = sqlite3_column_int64(select_statement.get(), 0);
        reminder.title = column_text(select_statement.get(), 1);
        reminder.due_at = column_text(select_statement.get(), 2);
        reminder.status = column_text(select_statement.get(), 3);
        reminder.event = std::strcmp(time_column, "start_time") == 0 ? "start" : "end";
        found.push_back(std::move(reminder));
    }
    if (rc != SQLITE_DONE) {
        error = sqlite3_errmsg(db);
        return false;
    }
    if (found.empty()) {
        return true;
    }

    const std::string update_sql =
        std::string("UPDATE tasks SET ") + notified_column +
        " = 1, updated_at = strftime('%Y-%m-%dT%H:%M:%S', 'now', 'localtime') "
        "WHERE id = ?;";

    Statement update_statement;
    if (!update_statement.prepare(db, update_sql, error)) {
        return false;
    }
    for (const Reminder& reminder : found) {
        sqlite3_reset(update_statement.get());
        sqlite3_clear_bindings(update_statement.get());
        sqlite3_bind_int64(update_statement.get(), 1, reminder.task_id);
        if (sqlite3_step(update_statement.get()) != SQLITE_DONE) {
            error = sqlite3_errmsg(db);
            return false;
        }
    }

    reminders.insert(reminders.end(),
                     std::make_move_iterator(found.begin()),
                     std::make_move_iterator(found.end()));
    return true;
}

bool collect_recurring_due(sqlite3* db, const std::string& now_iso,
                           const std::string& stale_before_iso,
                           std::vector<Reminder>& reminders,
                           std::string& error) {
    int64_t now_seconds = 0;
    int64_t stale_seconds = 0;
    if (!datetime_to_seconds(now_iso, now_seconds) ||
        !datetime_to_seconds(stale_before_iso, stale_seconds)) {
        error = "提醒时间格式无效";
        return false;
    }

    Statement select_statement;
    if (!select_statement.prepare(
            db,
            "SELECT id,title,start_time,end_time FROM tasks "
            "WHERE repeat_daily = 1 ORDER BY id ASC;",
            error)) {
        return false;
    }

    Statement status_statement;
    if (!status_statement.prepare(
            db,
            "SELECT status FROM task_day_status "
            "WHERE task_id = ? AND day_date = ?;",
            error)) {
        return false;
    }

    Statement insert_statement;
    if (!insert_statement.prepare(
            db,
            "INSERT OR IGNORE INTO reminder_events"
            "(task_id, occurrence_date, event, notified_at) "
            "VALUES (?, ?, ?, ?);",
            error)) {
        return false;
    }

    const std::string today = now_iso.substr(0, 10);
    const std::string yesterday = add_days_to_date(today, -1);
    const std::string days[] = {yesterday, today};

    int rc = SQLITE_ROW;
    while ((rc = sqlite3_step(select_statement.get())) == SQLITE_ROW) {
        const long long task_id = sqlite3_column_int64(select_statement.get(), 0);
        const std::string title = column_text(select_statement.get(), 1);
        const std::string base_start = column_text(select_statement.get(), 2);
        const std::string base_end = column_text(select_statement.get(), 3);

        int64_t base_start_seconds = 0;
        int64_t base_end_seconds = 0;
        if (!datetime_to_seconds(base_start, base_start_seconds) ||
            !datetime_to_seconds(base_end, base_end_seconds)) {
            error = "重复任务的时间格式无效";
            return false;
        }
        const int64_t duration = base_end_seconds - base_start_seconds;
        const std::string anchor_date = base_start.substr(0, 10);
        const std::string time_of_day = base_start.substr(11, 8);

        for (const std::string& day : days) {
            if (day < anchor_date) {
                continue;
            }

            sqlite3_reset(status_statement.get());
            sqlite3_clear_bindings(status_statement.get());
            sqlite3_bind_int64(status_statement.get(), 1, task_id);
            sqlite3_bind_text(status_statement.get(), 2, day.c_str(), -1,
                              SQLITE_TRANSIENT);
            const int status_rc = sqlite3_step(status_statement.get());
            std::string status = "pending";
            if (status_rc == SQLITE_ROW) {
                status = column_text(status_statement.get(), 0);
            } else if (status_rc != SQLITE_DONE) {
                error = sqlite3_errmsg(db);
                return false;
            }

            if (status != "pending" && status != "in_progress") {
                continue;
            }

            int64_t occurrence_start = 0;
            if (!datetime_to_seconds(day + "T" + time_of_day,
                                     occurrence_start)) {
                error = "重复任务的时间格式无效";
                return false;
            }
            const int64_t occurrence_end = occurrence_start + duration;

            struct DueEvent {
                const char* event;
                int64_t due_seconds;
            };
            const DueEvent events[] = {
                {"start", occurrence_start},
                {"end", occurrence_end},
            };

            for (const DueEvent& due : events) {
                if (due.due_seconds > now_seconds) {
                    continue;
                }

                sqlite3_reset(insert_statement.get());
                sqlite3_clear_bindings(insert_statement.get());
                sqlite3_bind_int64(insert_statement.get(), 1, task_id);
                sqlite3_bind_text(insert_statement.get(), 2, day.c_str(), -1,
                                  SQLITE_TRANSIENT);
                sqlite3_bind_text(insert_statement.get(), 3, due.event, -1,
                                  SQLITE_STATIC);
                sqlite3_bind_text(insert_statement.get(), 4, now_iso.c_str(), -1,
                                  SQLITE_TRANSIENT);
                if (sqlite3_step(insert_statement.get()) != SQLITE_DONE) {
                    error = sqlite3_errmsg(db);
                    return false;
                }
                if (sqlite3_changes(db) == 0) {
                    continue;
                }
                if (due.due_seconds <= stale_seconds) {
                    continue;
                }

                Reminder reminder;
                reminder.task_id = task_id;
                reminder.title = title;
                reminder.event = due.event;
                reminder.due_at = seconds_to_datetime(due.due_seconds);
                reminder.status = status;
                reminders.push_back(std::move(reminder));
            }
        }
    }

    if (rc != SQLITE_DONE) {
        error = sqlite3_errmsg(db);
        return false;
    }
    return true;
}

json reminders_to_json(std::vector<Reminder>& reminders) {
    std::sort(reminders.begin(), reminders.end(),
              [](const Reminder& lhs, const Reminder& rhs) {
                  if (lhs.due_at != rhs.due_at) {
                      return lhs.due_at < rhs.due_at;
                  }
                  if (lhs.event != rhs.event) {
                      return lhs.event == "start";
                  }
                  return lhs.task_id < rhs.task_id;
              });

    json result = json::array();
    for (const Reminder& reminder : reminders) {
        json item;
        item["task_id"] = reminder.task_id;
        item["title"] = reminder.title;
        item["event"] = reminder.event;
        item["due_at"] = reminder.due_at;
        item["status"] = reminder.status;
        result.push_back(std::move(item));
    }
    return result;
}

}  // namespace

extern "C" {

DP_API const char* dp_version(void) {
    return kVersion;
}

DP_API DpContext* dp_create(const char* db_path, char** error_message) {
    clear_error(error_message);
    if (db_path == nullptr || *db_path == '\0') {
        set_error(error_message, "数据库路径不能为空");
        return nullptr;
    }

    auto context = std::unique_ptr<DpContext>(new (std::nothrow) DpContext());
    if (!context) {
        set_error(error_message, "内存不足");
        return nullptr;
    }

    sqlite3* db = nullptr;
    const int rc = sqlite3_open_v2(
        db_path, &db,
        SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX,
        nullptr);
    if (rc != SQLITE_OK) {
        set_error(error_message, db != nullptr ? sqlite3_errmsg(db) : "无法打开数据库");
        if (db != nullptr) {
            sqlite3_close_v2(db);
        }
        return nullptr;
    }

    context->db = db;
    sqlite3_busy_timeout(db, 5000);

    std::string error;
    if (!exec_sql(db, "PRAGMA journal_mode=WAL;", error) ||
        !exec_sql(db, "PRAGMA synchronous=NORMAL;", error) ||
        !exec_sql(db, "PRAGMA foreign_keys=ON;", error) ||
        !exec_sql(db, kSchema, error) ||
        !ensure_schema(db, error)) {
        set_error(error_message, "初始化数据库失败：" + error);
        sqlite3_close_v2(db);
        return nullptr;
    }

    return context.release();
}

DP_API void dp_destroy(DpContext* ctx) {
    if (ctx == nullptr) {
        return;
    }
    {
        std::lock_guard<std::mutex> lock(ctx->mutex);
        if (ctx->db != nullptr) {
            sqlite3_close_v2(ctx->db);
            ctx->db = nullptr;
        }
    }
    delete ctx;
}

DP_API void dp_free_string(char* value) {
    std::free(value);
}

DP_API char* dp_list_tasks(DpContext* ctx, const char* from_iso, const char* to_iso,
                           char** error_message) {
    clear_error(error_message);
    if (ctx == nullptr) {
        set_error(error_message, "核心未初始化");
        return nullptr;
    }
    const std::string from = from_iso != nullptr ? from_iso : "";
    const std::string to = to_iso != nullptr ? to_iso : "";
    if (!is_valid_datetime(from) || !is_valid_datetime(to) || to <= from) {
        set_error(error_message, "时间范围格式无效");
        return nullptr;
    }

    try {
        std::lock_guard<std::mutex> lock(ctx->mutex);
        std::string error;
        json tasks = json::array();

        const std::string fixed_sql =
            std::string("SELECT ") + kTaskColumns +
            " FROM tasks WHERE repeat_daily = 0 "
            "AND start_time >= ? AND start_time < ? "
            "ORDER BY start_time ASC, id ASC;";
        Statement fixed_statement;
        if (!fixed_statement.prepare(ctx->db, fixed_sql, error)) {
            set_error(error_message, error);
            return nullptr;
        }
        sqlite3_bind_text(fixed_statement.get(), 1, from.c_str(), -1,
                          SQLITE_TRANSIENT);
        sqlite3_bind_text(fixed_statement.get(), 2, to.c_str(), -1,
                          SQLITE_TRANSIENT);

        int rc = SQLITE_ROW;
        while ((rc = sqlite3_step(fixed_statement.get())) == SQLITE_ROW) {
            tasks.push_back(task_from_row(fixed_statement.get()));
        }
        if (rc != SQLITE_DONE) {
            set_error_from_sqlite(error_message, ctx->db);
            return nullptr;
        }

        const std::string day = from.substr(0, 10);
        const std::string recurring_sql =
            "SELECT t.id,t.title,t.notes,t.start_time,t.end_time,"
            "COALESCE(s.status,'pending'),t.start_notified,t.end_notified,"
            "t.created_at,t.updated_at,t.repeat_daily "
            "FROM tasks t "
            "LEFT JOIN task_day_status s "
            "ON s.task_id = t.id AND s.day_date = ? "
            "WHERE t.repeat_daily = 1 AND substr(t.start_time,1,10) <= ? "
            "ORDER BY t.start_time ASC, t.id ASC;";
        Statement recurring_statement;
        if (!recurring_statement.prepare(ctx->db, recurring_sql, error)) {
            set_error(error_message, error);
            return nullptr;
        }
        sqlite3_bind_text(recurring_statement.get(), 1, day.c_str(), -1,
                          SQLITE_TRANSIENT);
        sqlite3_bind_text(recurring_statement.get(), 2, day.c_str(), -1,
                          SQLITE_TRANSIENT);

        rc = SQLITE_ROW;
        while ((rc = sqlite3_step(recurring_statement.get())) == SQLITE_ROW) {
            json task = task_from_row(recurring_statement.get());
            std::string occurrence_start;
            std::string occurrence_end;
            if (!make_occurrence(day, task["start_time"].get<std::string>(),
                                 task["end_time"].get<std::string>(),
                                 occurrence_start, occurrence_end)) {
                set_error(error_message, "重复任务的时间格式无效");
                return nullptr;
            }
            task["start_time"] = occurrence_start;
            task["end_time"] = occurrence_end;
            tasks.push_back(std::move(task));
        }
        if (rc != SQLITE_DONE) {
            set_error_from_sqlite(error_message, ctx->db);
            return nullptr;
        }

        std::sort(tasks.begin(), tasks.end(), [](const json& lhs, const json& rhs) {
            const std::string lhs_start = lhs["start_time"].get<std::string>();
            const std::string rhs_start = rhs["start_time"].get<std::string>();
            if (lhs_start != rhs_start) {
                return lhs_start < rhs_start;
            }
            return lhs["id"].get<long long>() < rhs["id"].get<long long>();
        });

        const std::string payload = tasks.dump();
        char* result = to_c_string(payload);
        if (result == nullptr) {
            set_error(error_message, "内存不足");
        }
        return result;
    } catch (const std::exception& exception) {
        set_error(error_message, std::string("内部错误：") + exception.what());
        return nullptr;
    }
}

DP_API char* dp_get_task(DpContext* ctx, long long id, char** error_message) {
    clear_error(error_message);
    if (ctx == nullptr) {
        set_error(error_message, "核心未初始化");
        return nullptr;
    }
    if (id <= 0) {
        set_error(error_message, "任务 ID 无效");
        return nullptr;
    }

    try {
        std::lock_guard<std::mutex> lock(ctx->mutex);
        json task;
        bool found = false;
        std::string error;
        if (!load_task_locked(ctx->db, id, task, found, error)) {
            set_error(error_message, error);
            return nullptr;
        }
        if (!found) {
            return nullptr;
        }
        char* result = to_c_string(task.dump());
        if (result == nullptr) {
            set_error(error_message, "内存不足");
        }
        return result;
    } catch (const std::exception& exception) {
        set_error(error_message, std::string("内部错误：") + exception.what());
        return nullptr;
    }
}

DP_API char* dp_create_task(DpContext* ctx, const char* task_json, char** error_message) {
    clear_error(error_message);
    if (ctx == nullptr) {
        set_error(error_message, "核心未初始化");
        return nullptr;
    }

    TaskInput input;
    std::string error;
    if (!parse_task_input(task_json, input, error)) {
        set_error(error_message, error);
        return nullptr;
    }

    try {
        std::lock_guard<std::mutex> lock(ctx->mutex);
        json task;
        if (!insert_task_locked(ctx, input, task, error)) {
            set_error(error_message, error);
            return nullptr;
        }
        char* result = to_c_string(task.dump());
        if (result == nullptr) {
            set_error(error_message, "内存不足");
        }
        return result;
    } catch (const std::exception& exception) {
        set_error(error_message, std::string("内部错误：") + exception.what());
        return nullptr;
    }
}

DP_API char* dp_update_task(DpContext* ctx, long long id, const char* task_json,
                            char** error_message) {
    clear_error(error_message);
    if (ctx == nullptr) {
        set_error(error_message, "核心未初始化");
        return nullptr;
    }
    if (id <= 0) {
        set_error(error_message, "任务 ID 无效");
        return nullptr;
    }

    TaskInput input;
    std::string error;
    if (!parse_task_input(task_json, input, error)) {
        set_error(error_message, error);
        return nullptr;
    }

    try {
        std::lock_guard<std::mutex> lock(ctx->mutex);
        json task;
        if (!update_task_locked(ctx, id, input, task, error)) {
            set_error(error_message, error);
            return nullptr;
        }
        char* result = to_c_string(task.dump());
        if (result == nullptr) {
            set_error(error_message, "内存不足");
        }
        return result;
    } catch (const std::exception& exception) {
        set_error(error_message, std::string("内部错误：") + exception.what());
        return nullptr;
    }
}

DP_API int dp_delete_task(DpContext* ctx, long long id, char** error_message) {
    clear_error(error_message);
    if (ctx == nullptr) {
        set_error(error_message, "核心未初始化");
        return 0;
    }
    if (id <= 0) {
        set_error(error_message, "任务 ID 无效");
        return 0;
    }

    try {
        std::lock_guard<std::mutex> lock(ctx->mutex);
        std::string error;
        Statement statement;
        if (!statement.prepare(ctx->db, "DELETE FROM tasks WHERE id = ?;", error)) {
            set_error(error_message, error);
            return 0;
        }
        sqlite3_bind_int64(statement.get(), 1, id);
        if (sqlite3_step(statement.get()) != SQLITE_DONE) {
            set_error_from_sqlite(error_message, ctx->db);
            return 0;
        }
        if (sqlite3_changes(ctx->db) == 0) {
            set_error(error_message, "任务不存在");
            return 0;
        }
        return 1;
    } catch (const std::exception& exception) {
        set_error(error_message, std::string("内部错误：") + exception.what());
        return 0;
    }
}

DP_API int dp_set_status(DpContext* ctx, long long id, const char* status,
                         const char* day_date, char** error_message) {
    clear_error(error_message);
    if (ctx == nullptr) {
        set_error(error_message, "核心未初始化");
        return 0;
    }
    if (id <= 0) {
        set_error(error_message, "任务 ID 无效");
        return 0;
    }
    const std::string next_status = status != nullptr ? status : "";
    if (!is_valid_status(next_status)) {
        set_error(error_message, status_check_list());
        return 0;
    }
    const std::string day = day_date != nullptr ? day_date : "";
    if (!day.empty() && !is_valid_date(day)) {
        set_error(error_message, "日期格式无效，应为 yyyy-MM-dd");
        return 0;
    }

    try {
        std::lock_guard<std::mutex> lock(ctx->mutex);

        Statement info_statement;
        std::string error;
        if (!info_statement.prepare(
                ctx->db,
                "SELECT repeat_daily,start_time FROM tasks WHERE id = ?;",
                error)) {
            set_error(error_message, error);
            return 0;
        }
        sqlite3_bind_int64(info_statement.get(), 1, id);
        const int info_rc = sqlite3_step(info_statement.get());
        if (info_rc == SQLITE_DONE) {
            set_error(error_message, "任务不存在");
            return 0;
        }
        if (info_rc != SQLITE_ROW) {
            set_error_from_sqlite(error_message, ctx->db);
            return 0;
        }
        const bool repeat_daily = sqlite3_column_int(info_statement.get(), 0) != 0;
        const std::string anchor_date =
            column_text(info_statement.get(), 1).substr(0, 10);

        if (repeat_daily) {
            if (day.empty()) {
                set_error(error_message, "重复任务需要指定日期");
                return 0;
            }
            if (day < anchor_date) {
                set_error(error_message, "该日期不在重复任务的范围内");
                return 0;
            }

            Statement day_statement;
            const char* day_sql = R"sql(
INSERT INTO task_day_status (task_id, day_date, status, updated_at)
VALUES (?, ?, ?, strftime('%Y-%m-%dT%H:%M:%S', 'now', 'localtime'))
ON CONFLICT(task_id, day_date) DO UPDATE SET
    status = excluded.status,
    updated_at = excluded.updated_at;
)sql";
            if (!day_statement.prepare(ctx->db, day_sql, error)) {
                set_error(error_message, error);
                return 0;
            }
            sqlite3_bind_int64(day_statement.get(), 1, id);
            sqlite3_bind_text(day_statement.get(), 2, day.c_str(), -1,
                              SQLITE_TRANSIENT);
            sqlite3_bind_text(day_statement.get(), 3, next_status.c_str(), -1,
                              SQLITE_TRANSIENT);
            if (sqlite3_step(day_statement.get()) != SQLITE_DONE) {
                set_error_from_sqlite(error_message, ctx->db);
                return 0;
            }

            Statement touch_statement;
            if (!touch_statement.prepare(
                    ctx->db,
                    "UPDATE tasks SET updated_at = "
                    "strftime('%Y-%m-%dT%H:%M:%S', 'now', 'localtime') "
                    "WHERE id = ?;",
                    error)) {
                set_error(error_message, error);
                return 0;
            }
            sqlite3_bind_int64(touch_statement.get(), 1, id);
            if (sqlite3_step(touch_statement.get()) != SQLITE_DONE) {
                set_error_from_sqlite(error_message, ctx->db);
                return 0;
            }
            return 1;
        }

        Statement statement;
        const char* sql =
            "UPDATE tasks SET status = ?, "
            "updated_at = strftime('%Y-%m-%dT%H:%M:%S', 'now', 'localtime') "
            "WHERE id = ?;";
        if (!statement.prepare(ctx->db, sql, error)) {
            set_error(error_message, error);
            return 0;
        }
        sqlite3_bind_text(statement.get(), 1, next_status.c_str(), -1, SQLITE_TRANSIENT);
        sqlite3_bind_int64(statement.get(), 2, id);
        if (sqlite3_step(statement.get()) != SQLITE_DONE) {
            set_error_from_sqlite(error_message, ctx->db);
            return 0;
        }
        if (sqlite3_changes(ctx->db) == 0) {
            set_error(error_message, "任务不存在");
            return 0;
        }
        return 1;
    } catch (const std::exception& exception) {
        set_error(error_message, std::string("内部错误：") + exception.what());
        return 0;
    }
}

DP_API char* dp_poll_reminders(DpContext* ctx, const char* now_iso,
                               const char* stale_before_iso, char** error_message) {
    clear_error(error_message);
    if (ctx == nullptr) {
        set_error(error_message, "核心未初始化");
        return nullptr;
    }
    const std::string now = now_iso != nullptr ? now_iso : "";
    const std::string stale_before = stale_before_iso != nullptr ? stale_before_iso : "";
    if (!is_valid_datetime(now) || !is_valid_datetime(stale_before) || stale_before > now) {
        set_error(error_message, "提醒时间格式无效");
        return nullptr;
    }

    try {
        std::lock_guard<std::mutex> lock(ctx->mutex);
        std::string error;
        Transaction transaction(ctx->db);
        if (!transaction.begin(error)) {
            set_error(error_message, error);
            return nullptr;
        }

        // Consume old events without returning them, so a restart does not
        // replay yesterday's reminders.
        const std::string stale_sql =
            "UPDATE tasks SET "
            "start_notified = CASE WHEN start_time <= ? THEN 1 ELSE start_notified END, "
            "end_notified = CASE WHEN end_time <= ? THEN 1 ELSE end_notified END, "
            "updated_at = strftime('%Y-%m-%dT%H:%M:%S', 'now', 'localtime') "
            "WHERE repeat_daily = 0 "
            "AND status IN ('pending','in_progress') "
            "AND ((start_notified = 0 AND start_time <= ?) "
            "  OR (end_notified = 0 AND end_time <= ?));";

        Statement stale_statement;
        if (!stale_statement.prepare(ctx->db, stale_sql, error)) {
            set_error(error_message, error);
            return nullptr;
        }
        sqlite3_bind_text(stale_statement.get(), 1, stale_before.c_str(), -1, SQLITE_TRANSIENT);
        sqlite3_bind_text(stale_statement.get(), 2, stale_before.c_str(), -1, SQLITE_TRANSIENT);
        sqlite3_bind_text(stale_statement.get(), 3, stale_before.c_str(), -1, SQLITE_TRANSIENT);
        sqlite3_bind_text(stale_statement.get(), 4, stale_before.c_str(), -1, SQLITE_TRANSIENT);
        if (sqlite3_step(stale_statement.get()) != SQLITE_DONE) {
            set_error_from_sqlite(error_message, ctx->db);
            return nullptr;
        }

        std::vector<Reminder> reminders;
        if (!collect_due(ctx->db, "start_time", "start_notified", now, reminders, error) ||
            !collect_due(ctx->db, "end_time", "end_notified", now, reminders, error) ||
            !collect_recurring_due(ctx->db, now, stale_before, reminders, error)) {
            set_error(error_message, error);
            return nullptr;
        }

        if (!transaction.commit(error)) {
            set_error(error_message, error);
            return nullptr;
        }

        const json payload = reminders_to_json(reminders);
        char* result = to_c_string(payload.dump());
        if (result == nullptr) {
            set_error(error_message, "内存不足");
        }
        return result;
    } catch (const std::exception& exception) {
        set_error(error_message, std::string("内部错误：") + exception.what());
        return nullptr;
    }
}

}  // extern "C"
