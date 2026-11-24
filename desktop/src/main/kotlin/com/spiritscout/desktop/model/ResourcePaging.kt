package com.spiritscout.desktop.model

object ResourcePaging {
    const val PAGE_SIZE = 100

    fun nextOffset(currentOffset: Int, pageSize: Int, pageCount: Int, hasMore: Boolean): Int? {
        return if (hasMore && pageCount > 0) currentOffset + pageCount else null
    }
}
