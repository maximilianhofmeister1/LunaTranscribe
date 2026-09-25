import Foundation

// Übersetzt die Zustände der Modell-Manager in die einheitliche `LoadStatus`-Darstellung,
// damit Menü, Übersicht und Einstellungen sie gleich anzeigen.

extension ModelManager {
    var loadStatus: LoadStatus {
        switch state {
        case .idle: return LoadStatus(phase: .idle, text: statusText)
        case .loading(let p): return LoadStatus(phase: .loading(p < 0.9 ? p / 0.9 : nil), text: statusText)
        case .ready(let kind): return LoadStatus(phase: .ready, text: "\(kind.shortLabel) bereit")
        case .failed(let message): return LoadStatus(phase: .failed, text: message)
        }
    }

    var vocabularyStatus: LoadStatus {
        switch vocabularyState {
        case .off:
            return LoadStatus(phase: .idle, text: "Wird aktiv, sobald das Modell geladen ist")
        case .loading:
            return LoadStatus(phase: .loading(nil), text: "Wörterbuch wird übernommen (beim ersten Mal lädt das Zusatzmodell)…")
        case .ready(let terms, let boosted):
            let count = "\(terms) \(terms == 1 ? "Begriff" : "Begriffe") aktiv"
            return LoadStatus(phase: .ready, text: count + (boosted ? " – mit akustischem Boosting" : " – nur Schreibweisen (Boosting nur mit Parakeet)"))
        case .failed(let message):
            return LoadStatus(phase: .warning, text: "Boosting nicht verfügbar, Schreibweisen werden trotzdem korrigiert: \(message)")
        }
    }
}

extension RefinerManager {
    var loadStatus: LoadStatus {
        switch state {
        case .idle: return LoadStatus(phase: .idle, text: statusText)
        case .loading(let p): return LoadStatus(phase: .loading(p < 0.9 ? p / 0.9 : nil), text: statusText)
        case .ready(let kind): return LoadStatus(phase: .ready, text: "\(kind.shortLabel) bereit")
        case .failed(let message): return LoadStatus(phase: .failed, text: message)
        }
    }
}
