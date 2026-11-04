#ifndef DAILYPLAN_CORE_H
#define DAILYPLAN_CORE_H

/*
 * DailyPlan native core (C ABI).
 *
 * All strings are UTF-8. Every char* returned by this library must be released
 * with dp_free_string(). Error messages are written to error_message on failure
 * and are also owned by the caller (release with dp_free_string()).
 *
 * Date-time strings use the local-time format yyyy-MM-ddTHH:mm:ss and are
 * compared lexicographically, so callers must always send this exact format.
 */

#if defined(_WIN32)
#  if defined(DAILYPLAN_CORE_BUILD)
#    define DP_API __declspec(dllexport)
#  else
#    define DP_API __declspec(dllimport)
#  endif
#else
#  define DP_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct DpContext DpContext;

/* Returns a static version string; do not free. */
DP_API const char* dp_version(void);

/*
 * Opens/creates the SQLite database and initialises the schema.
 * Returns NULL on failure.
 */
DP_API DpContext* dp_create(const char* db_path, char** error_message);

DP_API void dp_destroy(DpContext* ctx);

/* Frees a string allocated by this library. NULL is accepted. */
DP_API void dp_free_string(char* value);

/*
 * Lists tasks for [from_iso, to_iso). One-off tasks are selected when their
 * start_time falls inside the range. Daily repeating tasks are expanded into
 * one item per selected day, starting from their first date.
 * Returns a JSON array ordered by start time. The caller owns the string.
 */
DP_API char* dp_list_tasks(DpContext* ctx,
                           const char* from_iso,
                           const char* to_iso,
                           char** error_message);

/* Returns one task as a JSON object, or NULL if it does not exist. */
DP_API char* dp_get_task(DpContext* ctx,
                         long long id,
                         char** error_message);

/*
 * task_json: {"title","notes","start_time","end_time","status","repeat_daily"}
 * repeat_daily is optional and defaults to false. Repeating tasks must last at
 * most 24 hours and always start with a pending status; each day records its
 * own status separately.
 * Returns the created task as a JSON object.
 */
DP_API char* dp_create_task(DpContext* ctx,
                            const char* task_json,
                            char** error_message);

/*
 * Replaces the editable fields of a task. If the start/end time changes, the
 * corresponding reminder flag is reset. Returns the updated task JSON.
 */
DP_API char* dp_update_task(DpContext* ctx,
                            long long id,
                            const char* task_json,
                            char** error_message);

/* Returns 1 on success, 0 on failure. */
DP_API int dp_delete_task(DpContext* ctx,
                          long long id,
                          char** error_message);

/*
 * status: pending | in_progress | completed | cancelled
 * day_iso: yyyy-MM-dd. Required for repeating tasks (the status applies only
 * to that day); ignored for one-off tasks.
 */
DP_API int dp_set_status(DpContext* ctx,
                         long long id,
                         const char* status,
                         const char* day_iso,
                         char** error_message);

/*
 * Atomically claims due start/end reminders and returns them as a JSON array:
 * [{"task_id":1,"title":"...","event":"start|end","due_at":"...",
 *   "status":"..."}]
 *
 * Events at or before stale_before_iso are marked as notified but are not
 * returned, so restarting the app does not replay old reminders. The caller is
 * expected to pass a small grace window (for example now - 10 minutes).
 *
 * Daily repeating tasks emit start/end reminders for each day whose occurrence
 * status is still pending or in progress. Claims are de-duplicated per task,
 * occurrence date and event.
 */
DP_API char* dp_poll_reminders(DpContext* ctx,
                               const char* now_iso,
                               const char* stale_before_iso,
                               char** error_message);

#ifdef __cplusplus
}
#endif

#endif /* DAILYPLAN_CORE_H */
