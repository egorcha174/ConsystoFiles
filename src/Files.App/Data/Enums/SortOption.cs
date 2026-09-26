// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

namespace Files.App.Data.Enums
{
	public enum SortOption
	{
		/// <summary>
		/// Sort by name.
		/// </summary>
		Name = 0,

		/// <summary>
		/// Sort by date modified.
		/// </summary>
		DateModified = 1,

		/// <summary>
		/// Sort by date created.
		/// </summary>
		DateCreated = 2,

		/// <summary>
		/// Sort by size.
		/// </summary>
		Size = 3,

		/// <summary>
		/// Sort by file type.
		/// </summary>
		FileType = 4,

		/// <summary>
		/// Sort by sync status.
		/// </summary>
		/// <remarks>
		/// Reserved for cloud drives.
		/// </remarks>
		SyncStatus = 5,

		/// <summary>
		/// Sort by file tags.
		/// </summary>
		FileTag = 6,

		/// <summary>
		/// Sort by original folder.
		/// </summary>
		/// <remarks>
		/// Preserved for recycle bin.
		/// </remarks>
		OriginalFolder = 7,

		/// <summary>
		/// Sort by date deleted.
		/// </summary>
		/// <remarks>
		/// Preserved for recycle bin.
		/// </remarks>
		DateDeleted = 8,

		/// <summary>
		/// Sort by path.
		/// </summary>
		/// <remarks>
		/// Preserved for search results.
		/// </remarks>
		Path = 9,

		/// <summary>
		/// Sort by file extension (Consysto fork, details view extension column).
		/// </summary>
		FileExtension = 10,

		/// <summary>
		/// Sort by book author (Consysto fork, details view book columns).
		/// </summary>
		BookAuthor = 11,

		/// <summary>
		/// Sort by book series, then by the position in it (Consysto fork, details view book columns).
		/// </summary>
		BookSeries = 12,

		/// <summary>
		/// Sort by the part number of Inventor documents (Consysto fork, details view Inventor columns).
		/// </summary>
		CadPartNumber = 13,

		/// <summary>
		/// Sort by the material of Inventor documents (Consysto fork, details view Inventor columns).
		/// </summary>
		CadMaterial = 14,

		/// <summary>
		/// Sort by the mass of Inventor documents (Consysto fork, details view Inventor columns).
		/// </summary>
		CadMass = 15,

		/// <summary>
		/// Sort by the program version that wrote a drawing or model (Consysto fork, details view CAD columns).
		/// </summary>
		CadVersion = 16,

		/// <summary>
		/// Sort by print time in minutes.
		/// </summary>
		CadPrintTime = 17
	}
}
