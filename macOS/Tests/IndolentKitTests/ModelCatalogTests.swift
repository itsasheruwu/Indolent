import Foundation
import IndolentKit
import Testing

struct ModelCatalogTests {
    @Test
    func loadsOnlyVisibleShellModels() async throws {
        let tempDirectory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: tempDirectory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: tempDirectory) }

        let fileURL = tempDirectory.appendingPathComponent("models_cache.json")
        try """
        {
          "models": [
            {
              "slug": "gpt-5.4",
              "display_name": "GPT-5.4",
              "description": "Visible",
              "visibility": "list",
              "priority": 1,
              "shell_type": "shell_command",
              "default_reasoning_level": "low",
              "supported_reasoning_levels": [{"effort":"low","description":"Fast"}]
            },
            {
              "slug": "hidden",
              "display_name": "Hidden",
              "description": "Nope",
              "visibility": "hidden",
              "priority": 0,
              "shell_type": "shell_command",
              "default_reasoning_level": "low",
              "supported_reasoning_levels": []
            }
          ]
        }
        """.data(using: .utf8)?.write(to: fileURL)

        let service = CodexModelCatalogService(cacheURL: fileURL)
        let models = await service.loadAvailableModels()
        #expect(models.count == 1)
        #expect(models.first?.slug == "gpt-5.4")
    }
}
