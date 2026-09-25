import Foundation
#if canImport(SQLite3)
import SQLite3
#else
import CSQLite
#endif

/// Schmale Hülle um die SQLite-C-API – genau das, was der Suchindex braucht. Nicht threadsicher;
/// der Besitzer (`SearchIndex`, ein Actor) serialisiert alle Zugriffe.
final class SQLiteDatabase {
    private var handle: OpaquePointer?

    init(url: URL) throws {
        let flags = SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE
        let code = sqlite3_open_v2(url.path, &handle, flags, nil)
        guard code == SQLITE_OK else {
            let message = handle.map { String(cString: sqlite3_errmsg($0)) } ?? "open failed"
            sqlite3_close_v2(handle)
            throw SQLiteError(code: code, message: message)
        }
    }

    deinit {
        sqlite3_close_v2(handle)
    }

    func execute(_ sql: String) throws {
        var error: UnsafeMutablePointer<CChar>?
        let code = sqlite3_exec(handle, sql, nil, nil, &error)
        guard code == SQLITE_OK else {
            let message = error.map { String(cString: $0) } ?? lastErrorMessage
            sqlite3_free(error)
            throw SQLiteError(code: code, message: message)
        }
    }

    func prepare(_ sql: String) throws -> SQLiteStatement {
        var statement: OpaquePointer?
        let code = sqlite3_prepare_v2(handle, sql, -1, &statement, nil)
        guard code == SQLITE_OK, let statement else { throw SQLiteError(code: code, message: lastErrorMessage) }
        return SQLiteStatement(statement, database: self)
    }

    /// Führt `body` in einer Schreibtransaktion aus; bei einem Fehler wird alles zurückgerollt.
    func transaction<T>(_ body: () throws -> T) throws -> T {
        try execute("BEGIN IMMEDIATE")
        do {
            let result = try body()
            try execute("COMMIT")
            return result
        } catch {
            try? execute("ROLLBACK")
            throw error
        }
    }

    func scalarInt(_ sql: String, _ values: [SQLiteValue] = []) throws -> Int {
        let statement = try prepare(sql)
        try statement.bind(values)
        return try statement.step() ? statement.int(0) : 0
    }

    var lastErrorMessage: String { String(cString: sqlite3_errmsg(handle)) }
}

enum SQLiteValue {
    case int(Int)
    case double(Double)
    case text(String)
    case blob(Data)
    case null
}

struct SQLiteError: LocalizedError {
    let code: Int32
    let message: String
    var errorDescription: String? { "SQLite-Fehler \(code): \(message)" }
}

final class SQLiteStatement {
    private let statement: OpaquePointer
    private let database: SQLiteDatabase
    /// SQLite kopiert gebundene Texte und Blobs sofort (SQLITE_TRANSIENT).
    private static let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

    fileprivate init(_ statement: OpaquePointer, database: SQLiteDatabase) {
        self.statement = statement
        self.database = database
    }

    deinit {
        sqlite3_finalize(statement)
    }

    func bind(_ values: [SQLiteValue]) throws {
        sqlite3_reset(statement)
        sqlite3_clear_bindings(statement)
        for (offset, value) in values.enumerated() {
            let index = Int32(offset + 1)
            let code: Int32
            switch value {
            case .int(let v): code = sqlite3_bind_int64(statement, index, Int64(v))
            case .double(let v): code = sqlite3_bind_double(statement, index, v)
            case .text(let v): code = sqlite3_bind_text(statement, index, v, -1, Self.transient)
            case .blob(let v):
                code = v.withUnsafeBytes { bytes in
                    sqlite3_bind_blob(statement, index, bytes.baseAddress, Int32(v.count), Self.transient)
                }
            case .null: code = sqlite3_bind_null(statement, index)
            }
            guard code == SQLITE_OK else { throw SQLiteError(code: code, message: database.lastErrorMessage) }
        }
    }

    /// Ein Schritt; `true`, solange eine Ergebniszeile anliegt.
    func step() throws -> Bool {
        switch sqlite3_step(statement) {
        case SQLITE_ROW: return true
        case SQLITE_DONE: return false
        case let code: throw SQLiteError(code: code, message: database.lastErrorMessage)
        }
    }

    /// Bindet, führt bis zum Ende aus und verwirft Ergebniszeilen.
    func run(_ values: [SQLiteValue] = []) throws {
        try bind(values)
        while try step() {}
    }

    /// Bindet und liefert alle Zeilen, umgewandelt durch `row`.
    func query<T>(_ values: [SQLiteValue] = [], _ row: (SQLiteStatement) throws -> T) throws -> [T] {
        try bind(values)
        var result: [T] = []
        while try step() { result.append(try row(self)) }
        return result
    }

    func int(_ column: Int32) -> Int { Int(sqlite3_column_int64(statement, column)) }
    func double(_ column: Int32) -> Double { sqlite3_column_double(statement, column) }
    func isNull(_ column: Int32) -> Bool { sqlite3_column_type(statement, column) == SQLITE_NULL }

    func string(_ column: Int32) -> String {
        guard let text = sqlite3_column_text(statement, column) else { return "" }
        return String(cString: text)
    }

    func blob(_ column: Int32) -> Data {
        let count = Int(sqlite3_column_bytes(statement, column))
        guard count > 0, let bytes = sqlite3_column_blob(statement, column) else { return Data() }
        return Data(bytes: bytes, count: count)
    }
}
