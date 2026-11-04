cmake_minimum_required(VERSION 3.21)

project(DailyPlanCore VERSION 1.0.0 LANGUAGES C CXX)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "Build type" FORCE)
endif()

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(DAILYPLAN_OUTPUT_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../artifacts/native")
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY "${DAILYPLAN_OUTPUT_DIR}")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${DAILYPLAN_OUTPUT_DIR}")
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${DAILYPLAN_OUTPUT_DIR}")

# Prefer a vendored amalgamation when present. Otherwise use an installed
# SQLite development package (the setup script installs one via MSYS2).
if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/third_party/sqlite3/sqlite3.c")
    add_library(sqlite3 STATIC
        third_party/sqlite3/sqlite3.c
    )
    target_include_directories(sqlite3 PUBLIC third_party/sqlite3)
    target_compile_definitions(sqlite3 PRIVATE
        SQLITE_THREADSAFE=1
        SQLITE_OMIT_LOAD_EXTENSION
        SQLITE_DQS=0
        SQLITE_DEFAULT_WAL_SYNCHRONOUS=1
        SQLITE_USE_URI=1
    )
    set(DAILYPLAN_SQLITE_TARGET sqlite3)
elseif(MINGW AND DAILYPLAN_MSYS2_PREFIX)
    # Link the static archive explicitly: -lsqlite3 would pick the DLL import
    # library first and make the app depend on libsqlite3-0.dll at runtime.
    set(DAILYPLAN_SQLITE_LIBRARY
        "${DAILYPLAN_MSYS2_PREFIX}/lib/libsqlite3.a")
    if(NOT EXISTS "${DAILYPLAN_SQLITE_LIBRARY}")
        message(FATAL_ERROR
            "SQLite static library not found at ${DAILYPLAN_SQLITE_LIBRARY}. "
            "Run scripts/setup-toolchain.ps1.")
    endif()
    set(DAILYPLAN_SQLITE_INCLUDE_DIR "${DAILYPLAN_MSYS2_PREFIX}/include")
    set(DAILYPLAN_SQLITE_TARGET "${DAILYPLAN_SQLITE_LIBRARY}")
else()
    find_package(SQLite3 REQUIRED)
    set(DAILYPLAN_SQLITE_TARGET SQLite::SQLite3)
endif()

add_library(dailyplan_core SHARED
    src/core.cpp
)
target_include_directories(dailyplan_core
    PUBLIC include
    PRIVATE third_party ${DAILYPLAN_SQLITE_INCLUDE_DIR}
)
target_link_libraries(dailyplan_core PRIVATE ${DAILYPLAN_SQLITE_TARGET})
target_compile_definitions(dailyplan_core PRIVATE DAILYPLAN_CORE_BUILD)

if(MSVC)
    target_compile_options(dailyplan_core PRIVATE /W4 /permissive- /utf-8)
    if(TARGET sqlite3)
        target_compile_options(sqlite3 PRIVATE /W3 /utf-8)
    endif()
else()
    target_compile_options(dailyplan_core PRIVATE -Wall -Wextra -Wpedantic)
    if(TARGET sqlite3)
        target_compile_options(sqlite3 PRIVATE -w)
    endif()
endif()

if(MINGW)
    # Keep the DLL self-contained so it can be copied next to the C# app
    # without shipping MinGW runtime DLLs.
    target_link_options(dailyplan_core PRIVATE
        -static
        -static-libgcc
        -static-libstdc++
    )
    if(TARGET sqlite3)
        target_link_options(sqlite3 PRIVATE -static-libgcc)
    endif()
endif()

set_target_properties(dailyplan_core PROPERTIES
    OUTPUT_NAME "DailyPlanCore"
    PREFIX ""
)
